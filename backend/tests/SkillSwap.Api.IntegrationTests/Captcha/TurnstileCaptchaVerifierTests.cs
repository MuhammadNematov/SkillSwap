using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;
using SkillSwap.Infrastructure.Captcha;

namespace SkillSwap.Api.IntegrationTests.Captcha;

/// <summary>Клиент Cloudflare Turnstile против поддельного сервера: без запуска приложения и без БД.</summary>
public class TurnstileCaptchaVerifierTests
{
    private const string Secret = "1x0000000000000000000000000000000AA";

    private sealed class StubHttpClientFactory(HttpMessageHandler handler, TimeSpan timeout) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = timeout };
    }

    private readonly FakeCloudflareHandler _cloudflare = new() { ExpectedSecret = Secret };

    private TurnstileCaptchaVerifier Verifier(HttpMessageHandler? handler = null, TimeSpan? timeout = null, TurnstileOptions? options = null) =>
        new(
            new StubHttpClientFactory(handler ?? _cloudflare, timeout ?? TimeSpan.FromSeconds(5)),
            Options.Create(options ?? new TurnstileOptions { SecretKey = Secret }),
            NullLogger<TurnstileCaptchaVerifier>.Instance);

    // ---------- что отправляется Cloudflare ----------

    [Fact]
    public async Task It_posts_the_secret_and_the_token_as_a_form_to_the_siteverify_url()
    {
        var token = FakeCloudflareHandler.PassToken();

        var result = await Verifier().VerifyAsync(token, null);

        Assert.Equal(CaptchaOutcome.Passed, result.Outcome);
        var call = Assert.Single(_cloudflare.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://challenges.cloudflare.com/turnstile/v0/siteverify", call.Url.ToString());
        Assert.Equal("application/x-www-form-urlencoded", call.ContentType);
        Assert.Equal(Secret, call.Form["secret"]);
        Assert.Equal(token, call.Form["response"]);
    }

    [Fact]
    public async Task The_client_ip_is_not_sent_unless_the_caller_passes_it()
    {
        await Verifier().VerifyAsync(FakeCloudflareHandler.PassToken(), null);

        Assert.DoesNotContain("remoteip", Assert.Single(_cloudflare.Calls).Form.Keys);
    }

    [Fact]
    public async Task The_client_ip_is_sent_when_the_caller_passes_it()
    {
        await Verifier().VerifyAsync(FakeCloudflareHandler.PassToken(), "203.0.113.7");

        Assert.Equal("203.0.113.7", Assert.Single(_cloudflare.Calls).Form["remoteip"]);
    }

    [Fact]
    public async Task The_configured_url_is_used()
    {
        var options = new TurnstileOptions { SecretKey = Secret, VerifyUrl = "https://captcha.example.test/verify" };

        await Verifier(options: options).VerifyAsync(FakeCloudflareHandler.PassToken(), null);

        Assert.Equal("https://captcha.example.test/verify", Assert.Single(_cloudflare.Calls).Url.ToString());
    }

    [Fact]
    public async Task Special_characters_in_the_token_survive_form_encoding()
    {
        var token = "pass-a+b&c=d/e%20 é" + Guid.NewGuid().ToString("N");

        var result = await Verifier().VerifyAsync(token, null);

        Assert.Equal(CaptchaOutcome.Passed, result.Outcome);
        Assert.Equal(token, Assert.Single(_cloudflare.Calls).Form["response"]);
    }

    // ---------- когда в Cloudflare не ходим ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_token_is_reported_without_calling_cloudflare(string? token)
    {
        var result = await Verifier().VerifyAsync(token, null);

        Assert.Equal(CaptchaOutcome.Missing, result.Outcome);
        Assert.Empty(_cloudflare.Calls);
    }

    [Fact]
    public async Task An_oversized_token_is_rejected_without_calling_cloudflare()
    {
        var result = await Verifier().VerifyAsync(new string('a', TurnstileCaptchaVerifier.MaxTokenLength + 1), null);

        Assert.Equal(CaptchaOutcome.Invalid, result.Outcome);
        Assert.Empty(_cloudflare.Calls);
    }

    [Fact]
    public async Task A_token_of_exactly_the_maximum_length_is_still_sent()
    {
        var result = await Verifier().VerifyAsync(new string('a', TurnstileCaptchaVerifier.MaxTokenLength), null);

        Assert.Equal(CaptchaOutcome.Invalid, result.Outcome);   // заглушка не знает такой токен
        Assert.Single(_cloudflare.Calls);
    }

    // ---------- ответы Cloudflare ----------

    [Fact]
    public async Task A_token_used_twice_fails_the_second_time_like_the_real_service()
    {
        var token = FakeCloudflareHandler.PassToken();

        Assert.Equal(CaptchaOutcome.Passed, (await Verifier().VerifyAsync(token, null)).Outcome);
        var second = await Verifier().VerifyAsync(token, null);

        Assert.Equal(CaptchaOutcome.Invalid, second.Outcome);
        Assert.Equal(["timeout-or-duplicate"], second.ErrorCodes);
    }

    [Fact]
    public async Task A_token_cloudflare_does_not_accept_is_invalid_with_its_error_code()
    {
        var result = await Verifier().VerifyAsync("some-bot-token", null);

        Assert.Equal(CaptchaOutcome.Invalid, result.Outcome);
        Assert.Equal(["invalid-input-response"], result.ErrorCodes);
    }

    [Fact]
    public async Task An_unknown_error_code_is_treated_as_an_invalid_token_so_the_user_can_retry()
    {
        var result = await Verifier().VerifyAsync("unknown-code", null);

        Assert.Equal(CaptchaOutcome.Invalid, result.Outcome);
        Assert.Equal(["something-new"], result.ErrorCodes);
    }

    [Fact]
    public async Task A_failure_without_any_error_code_is_invalid()
    {
        var result = await Verifier().VerifyAsync("empty-object", null);

        Assert.Equal(CaptchaOutcome.Invalid, result.Outcome);
        Assert.Empty(result.ErrorCodes);
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("internal")]
    public async Task Errors_that_are_not_the_users_fault_make_the_check_unavailable(string token)
    {
        var result = await Verifier().VerifyAsync(token, null);

        Assert.Equal(CaptchaOutcome.Unavailable, result.Outcome);
        Assert.NotEmpty(result.ErrorCodes);
    }

    [Fact]
    public async Task A_wrong_secret_in_our_configuration_is_unavailable_not_an_invalid_user_token()
    {
        // Секрет в настройках неверный: Cloudflare ответит invalid-input-secret, и страдать будут все пользователи.
        var result = await Verifier(options: new TurnstileOptions { SecretKey = "wrong" }).VerifyAsync(FakeCloudflareHandler.PassToken(), null);

        Assert.Equal(CaptchaOutcome.Unavailable, result.Outcome);
        Assert.Equal(["invalid-input-secret"], result.ErrorCodes);
    }

    [Fact]
    public async Task An_error_of_ours_wins_over_user_errors_in_the_same_answer()
    {
        var handler = new ScriptedHandler(_ => Json("""{"success":false,"error-codes":["invalid-input-response","bad-request"]}"""));

        var result = await Verifier(handler).VerifyAsync("x", null);

        Assert.Equal(CaptchaOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task Extra_fields_in_a_successful_answer_are_ignored()
    {
        var handler = new ScriptedHandler(_ => Json(
            """{"success":true,"challenge_ts":"2026-11-17T12:00:00Z","hostname":"x","error-codes":[],"action":"login","cdata":"y","metadata":{"ephemeral_id":"z"},"new_field":1}"""));

        Assert.Equal(CaptchaOutcome.Passed, (await Verifier(handler).VerifyAsync("x", null)).Outcome);
    }

    [Fact]
    public async Task Success_true_wins_even_if_error_codes_are_present()
    {
        var handler = new ScriptedHandler(_ => Json("""{"success":true,"error-codes":["something"]}"""));

        Assert.Equal(CaptchaOutcome.Passed, (await Verifier(handler).VerifyAsync("x", null)).Outcome);
    }

    // ---------- Cloudflare недоступен: закрыто, а не «пропустить» ----------

    [Theory]
    [InlineData("outage")]
    [InlineData("http-500")]
    [InlineData("garbage")]
    public async Task When_cloudflare_cannot_be_asked_the_check_is_unavailable_and_not_passed(string token)
    {
        var result = await Verifier().VerifyAsync(token, null);

        Assert.Equal(CaptchaOutcome.Unavailable, result.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Any_non_success_http_status_is_unavailable(HttpStatusCode status)
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(status));

        var result = await Verifier(handler).VerifyAsync("x", null);

        Assert.Equal(CaptchaOutcome.Unavailable, result.Outcome);
        Assert.Equal([$"http-{(int)status}"], result.ErrorCodes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"success\"")]
    public async Task An_empty_or_unexpected_json_body_is_unavailable(string body)
    {
        var handler = new ScriptedHandler(_ => Json(body));

        var result = await Verifier(handler).VerifyAsync("x", null);

        Assert.Equal(CaptchaOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task A_cloudflare_that_does_not_answer_in_time_is_unavailable()
    {
        var result = await Verifier(timeout: TimeSpan.FromMilliseconds(200)).VerifyAsync("slow", null);

        Assert.Equal(CaptchaOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task When_the_caller_cancels_the_request_the_cancellation_is_not_mistaken_for_a_captcha_failure()
    {
        using var cts = new CancellationTokenSource();
        var pending = Verifier().VerifyAsync("slow", null, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // ---------- вспомогательное ----------

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
