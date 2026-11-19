using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Auth;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>Подтверждение email шестизначным кодом из письма (вместе со ссылкой): настоящая БД, письмо, лимиты и параллельный перебор.</summary>
[Collection(ApiCollection.Name)]
public class EmailConfirmationCodeApiTests
{
    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public EmailConfirmationCodeApiTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory, TestIps.Next());   // свой IP: лимит подтверждений (20 в час) не делится с другими тестами
    }

    private Task<HttpResponseMessage> ConfirmByCodeAsync(string email, string code, AuthApi? api = null) =>
        (api ?? _api).PostAsync("/auth/confirm-email/code", new { email, code });

    private string CodeOf(string email) =>
        Regex.Match(_factory.Mail.To(email).Last().Body, @": (\d{6})\r?\n").Groups[1].Value;

    private async Task<string> RegisteredAsync()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();
        return email;
    }

    private static string Wrong(string right) => right == "000000" ? "000001" : "000000";

    private Task<(string? Hash, int Attempts, DateTime? ExpiresAt, bool Confirmed)> RowAsync(string email) =>
        _factory.QueryAsync(async db =>
        {
            var u = await db.Users.AsNoTracking().SingleAsync(x => x.Email == email);
            return (u.EmailCodeHash, u.EmailCodeAttempts, u.EmailCodeExpiresAt, u.EmailConfirmed);
        });

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    // ---------- письмо ----------

    [Fact]
    public async Task The_letter_has_the_link_and_a_six_digit_code_and_the_database_keeps_only_a_hash_with_a_fifteen_minute_lifetime()
    {
        var email = await RegisteredAsync();

        var body = _factory.Mail.To(email).Single().Body;
        var code = CodeOf(email);
        Assert.Contains("/confirm-email?userId=", body);
        Assert.Matches(@"^\d{6}$", code);
        var (hash, attempts, expiresAt, confirmed) = await RowAsync(email);
        Assert.False(confirmed);
        Assert.Equal(0, attempts);
        Assert.Equal(EmailConfirmationCodes.Hash(Guid.Parse(_factory.Mail.LastConfirmationLink(email).UserId), code), hash);
        Assert.DoesNotContain(code, hash!);
        Assert.Equal(_factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(15), expiresAt!.Value, TimeSpan.FromSeconds(5));
    }

    // ---------- подтверждение ----------

    [Fact]
    public async Task The_right_code_confirms_the_email_and_the_user_can_sign_in()
    {
        var email = await RegisteredAsync();
        Assert.NotEqual(HttpStatusCode.OK, (await _api.LoginAsync(email)).StatusCode);   // до подтверждения войти нельзя

        var response = await ConfirmByCodeAsync(email, CodeOf(email));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var (hash, _, _, confirmed) = await RowAsync(email);
        Assert.True(confirmed);
        Assert.Null(hash);   // использованный код не хранится
        Assert.Equal(HttpStatusCode.OK, (await _api.LoginAsync(email)).StatusCode);
    }

    [Fact]
    public async Task A_code_pasted_with_a_space_in_the_middle_works()
    {
        var email = await RegisteredAsync();
        var code = CodeOf(email);

        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmByCodeAsync(email, $"{code[..3]} {code[3..]}")).StatusCode);
    }

    [Fact]
    public async Task The_link_and_the_code_are_independent_ways_and_the_link_removes_the_code()
    {
        var email = await RegisteredAsync();
        var (userId, token) = _factory.Mail.LastConfirmationLink(email);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.ConfirmEmailAsync(userId, token)).StatusCode);

        Assert.Null((await RowAsync(email)).Hash);
        var late = await ConfirmByCodeAsync(email, CodeOf(email));   // уже подтверждён: как неверный код
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Equal("invalid_confirmation_code", await ErrorCodeAsync(late));
    }

    // ---------- ошибки и попытки ----------

    [Fact]
    public async Task A_wrong_code_is_a_bad_request_and_costs_one_attempt_the_right_one_still_works_after_it()
    {
        var email = await RegisteredAsync();
        var right = CodeOf(email);

        var wrong = await ConfirmByCodeAsync(email, Wrong(right));

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("invalid_confirmation_code", await ErrorCodeAsync(wrong));
        Assert.Equal(1, (await RowAsync(email)).Attempts);
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmByCodeAsync(email, right)).StatusCode);
    }

    [Fact]
    public async Task After_five_wrong_tries_even_the_right_code_is_refused_until_a_new_letter()
    {
        var email = await RegisteredAsync();
        var right = CodeOf(email);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmByCodeAsync(email, Wrong(right))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmByCodeAsync(email, right)).StatusCode);
        Assert.Equal(5, (await RowAsync(email)).Attempts);   // шестая попытка не списана: код уже закрыт

        (await _api.ResendConfirmationAsync(email)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmByCodeAsync(email, CodeOf(email))).StatusCode);
    }

    [Fact]
    public async Task Parallel_guessing_cannot_get_more_than_five_attempts()
    {
        var email = await RegisteredAsync();
        var right = CodeOf(email);
        var tasks = Enumerable.Range(0, 40).Select(async i =>
        {
            // у каждого запроса свой IP, чтобы упереться именно в лимит попыток на код, а не в лимит по IP
            var api = new AuthApi(_factory, TestIps.Next());
            return await ConfirmByCodeAsync(email, Wrong(right), api);
        });

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Equal(5, (await RowAsync(email)).Attempts);
        Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmByCodeAsync(email, right)).StatusCode);   // код закрыт
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var email = await RegisteredAsync();
        var code = CodeOf(email);
        await _factory.QueryAsync(async db =>
        {
            var past = _factory.Clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
            await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailCodeExpiresAt, past));
            return 0;
        });

        var response = await ConfirmByCodeAsync(email, code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_confirmation_code", await ErrorCodeAsync(response));
        Assert.False((await RowAsync(email)).Confirmed);
    }

    [Fact]
    public async Task A_new_letter_replaces_the_code()
    {
        var email = await RegisteredAsync();
        var old = CodeOf(email);

        (await _api.ResendConfirmationAsync(email)).EnsureSuccessStatusCode();
        var fresh = CodeOf(email);

        Assert.Equal(2, _factory.Mail.To(email).Count);
        if (fresh != old)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await ConfirmByCodeAsync(email, old)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await ConfirmByCodeAsync(email, fresh)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_address_gives_the_same_answer_as_a_wrong_code()
    {
        var known = await RegisteredAsync();

        var unknown = await ConfirmByCodeAsync(AuthApi.NewEmail(), "123456");
        var wrong = await ConfirmByCodeAsync(known, Wrong(CodeOf(known)));

        Assert.Equal(unknown.StatusCode, wrong.StatusCode);
        Assert.Equal(await ErrorCodeAsync(unknown), await ErrorCodeAsync(wrong));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    [InlineData("")]
    public async Task A_code_of_the_wrong_shape_is_a_validation_error_and_costs_no_attempt(string code)
    {
        var email = await RegisteredAsync();

        var response = await ConfirmByCodeAsync(email, code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await ErrorCodeAsync(response));
        Assert.Equal(0, (await RowAsync(email)).Attempts);
    }
}
