using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.RateLimiting;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Api.IntegrationTests.Captcha;

/// <summary>
/// Капча Turnstile на register, login и forgot-password (FR-5), на настоящем приложении и PostgreSQL;
/// вместо Cloudflare — заглушка. Главное: проверка идёт ДО всей остальной логики.
/// Лимит по IP включён, чтобы проверить и порядок защит. Каждый тест «приходит» со своего IP.
/// </summary>
[Collection(OwnHostsCollection.Name)]
public class TurnstileFlowTests : IClassFixture<CaptchaApiFactory>
{
    private readonly CaptchaApiFactory _factory;

    public TurnstileFlowTests(CaptchaApiFactory factory)
    {
        _factory = factory;
    }

    private static string Pass() => FakeCloudflareHandler.PassToken();

    /// <summary>Клиент с «решающим» капчу виджетом: к каждому запросу прикладывает свежий токен.</summary>
    private AuthApi NewClient() => new(_factory, TestIps.Next()) { DefaultCaptcha = Pass };

    private int Calls => _factory.Cloudflare.Calls.Count;

    private Task<bool> UserExistsAsync(string email) =>
        _factory.QueryAsync(db => db.Set<User>().AnyAsync(u => u.Email == email));

    private static async Task AssertCaptchaErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, json.GetProperty("status").GetInt32());
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("title").GetString()));
    }

    /// <summary>Запрос регистрации БЕЗ поля captchaToken вовсе.</summary>
    private static Task<HttpResponseMessage> RegisterWithoutTokenFieldAsync(AuthApi api, string email, string password = AuthApi.Password) =>
        api.PostAsync("/auth/register", new
        {
            email,
            password,
            name = "Test User",
            dateOfBirth = "2000-01-01",
            acceptTerms = true,
            acceptChatModeration = true,
        });

    // ---------- register ----------

    [Fact]
    public async Task Register_with_a_passed_captcha_works_and_cloudflare_gets_the_secret_and_the_token()
    {
        var api = NewClient();
        var token = Pass();
        var email = AuthApi.NewEmail();
        var before = Calls;

        var response = await api.RegisterAsync(email, captchaToken: token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var call = Assert.Single(_factory.Cloudflare.Calls.Skip(before));
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://challenges.cloudflare.com/turnstile/v0/siteverify", call.Url.ToString());
        Assert.Equal(ApiFactory.TurnstileTestSecret, call.Form["secret"]);
        Assert.Equal(token, call.Form["response"]);
        Assert.DoesNotContain("remoteip", call.Form.Keys);   // IP клиента по умолчанию Cloudflare не отдаём
        Assert.True(await UserExistsAsync(email));
    }

    [Fact]
    public async Task Register_without_a_token_is_400_captcha_required_and_cloudflare_is_not_asked()
    {
        var api = NewClient();
        var email = AuthApi.NewEmail();
        var before = Calls;

        var response = await RegisterWithoutTokenFieldAsync(api, email);

        await AssertCaptchaErrorAsync(response, HttpStatusCode.BadRequest, "captcha_required");
        Assert.Equal(before, Calls);
        Assert.False(await UserExistsAsync(email));
        Assert.Empty(_factory.Mail.To(email));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_token_counts_as_missing(string token)
    {
        var api = NewClient();
        var before = Calls;

        var response = await api.RegisterAsync(AuthApi.NewEmail(), captchaToken: token);

        await AssertCaptchaErrorAsync(response, HttpStatusCode.BadRequest, "captcha_required");
        Assert.Equal(before, Calls);
    }

    [Fact]
    public async Task Register_with_a_token_cloudflare_rejects_is_400_captcha_invalid_and_creates_nothing()
    {
        var api = NewClient();
        var email = AuthApi.NewEmail();

        var response = await api.RegisterAsync(email, captchaToken: "bot-token");

        await AssertCaptchaErrorAsync(response, HttpStatusCode.BadRequest, "captcha_invalid");
        Assert.False(await UserExistsAsync(email));
        Assert.Empty(_factory.Mail.To(email));
    }

    [Fact]
    public async Task An_oversized_token_is_rejected_without_bothering_cloudflare()
    {
        var api = NewClient();
        var before = Calls;

        var response = await api.RegisterAsync(AuthApi.NewEmail(), captchaToken: new string('a', 2049));

        await AssertCaptchaErrorAsync(response, HttpStatusCode.BadRequest, "captcha_invalid");
        Assert.Equal(before, Calls);
    }

    [Fact]
    public async Task A_token_works_only_once_a_replayed_one_is_rejected()
    {
        var api = NewClient();
        var token = Pass();

        var first = await api.RegisterAsync(AuthApi.NewEmail(), captchaToken: token);
        var replay = await api.RegisterAsync(AuthApi.NewEmail(), captchaToken: token);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertCaptchaErrorAsync(replay, HttpStatusCode.BadRequest, "captcha_invalid");
    }

    // ---------- порядок: капча раньше всей остальной логики ----------

    [Fact]
    public async Task Captcha_comes_before_the_duplicate_email_check_so_registered_emails_cannot_be_probed()
    {
        var api = NewClient();
        var email = AuthApi.NewEmail();
        (await api.RegisterAsync(email)).EnsureSuccessStatusCode();

        var withoutCaptcha = await api.RegisterAsync(email, captchaToken: "bot-token");
        var withCaptcha = await api.RegisterAsync(email);

        // Без решённой капчи email неотличим от свободного: 409 увидит только тот, кто прошёл капчу.
        await AssertCaptchaErrorAsync(withoutCaptcha, HttpStatusCode.BadRequest, "captcha_invalid");
        await AssertCaptchaErrorAsync(withCaptcha, HttpStatusCode.Conflict, "email_already_registered");
    }

    [Fact]
    public async Task Captcha_comes_before_request_validation()
    {
        var api = NewClient();
        var badBody = AuthApi.NewEmail();

        var missing = await RegisterWithoutTokenFieldAsync(api, badBody, password: "short");
        var invalid = await api.RegisterAsync("not-an-email", captchaToken: "bot-token");
        var passed = await api.RegisterAsync("not-an-email");

        await AssertCaptchaErrorAsync(missing, HttpStatusCode.BadRequest, "captcha_required");   // а не validation_failed
        await AssertCaptchaErrorAsync(invalid, HttpStatusCode.BadRequest, "captcha_invalid");
        await AssertCaptchaErrorAsync(passed, HttpStatusCode.BadRequest, "validation_failed");   // с капчей — обычная валидация
    }

    // ---------- login ----------

    [Fact]
    public async Task Login_with_a_passed_captcha_works()
    {
        var email = await NewClient().RegisterConfirmedUserAsync();
        var api = NewClient();

        var response = await api.LoginAsync(email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(AuthApi.RefreshTokenFrom(response));
    }

    [Fact]
    public async Task Login_without_captcha_does_not_even_look_at_the_password()
    {
        var email = await NewClient().RegisterConfirmedUserAsync();
        var api = NewClient();

        // Верные учётные данные, но капча не пройдена: сеанса нет, вход не засчитан.
        var response = await api.LoginAsync(email, captchaToken: "bot-token");

        await AssertCaptchaErrorAsync(response, HttpStatusCode.BadRequest, "captcha_invalid");
        Assert.Null(AuthApi.SetCookieHeader(response));
        var user = await _factory.QueryAsync(db => db.Set<User>().SingleAsync(u => u.Email == email));
        Assert.Null(user.LastActiveAt);
        Assert.Equal(0, await _factory.QueryAsync(db => db.Set<RefreshToken>().CountAsync(t => t.UserId == user.Id)));
    }

    [Fact]
    public async Task Wrong_passwords_without_captcha_are_not_counted_so_they_cannot_lock_an_account()
    {
        var email = await NewClient().RegisterConfirmedUserAsync();
        var api = NewClient();

        // Шесть попыток подбора без капчи: Identity блокирует аккаунт после пяти НЕВЕРНЫХ паролей, но до пароля не доходит.
        for (var i = 0; i < 6; i++)
        {
            var attempt = await api.LoginAsync(email, "wrong-pass1", captchaToken: "bot-token");
            await AssertCaptchaErrorAsync(attempt, HttpStatusCode.BadRequest, "captcha_invalid");
        }

        var legitimate = await api.LoginAsync(email);   // владелец с настоящим паролем и капчей входит свободно
        Assert.Equal(HttpStatusCode.OK, legitimate.StatusCode);
    }

    [Fact]
    public async Task Without_captcha_login_does_not_reveal_whether_the_account_exists_or_is_confirmed()
    {
        var unconfirmed = AuthApi.NewEmail();
        (await NewClient().RegisterAsync(unconfirmed)).EnsureSuccessStatusCode();
        var api = NewClient();

        var forUnknown = await api.LoginAsync(AuthApi.NewEmail(), captchaToken: "bot-token");
        var forUnconfirmed = await api.LoginAsync(unconfirmed, captchaToken: "bot-token");
        var unconfirmedPassed = await api.LoginAsync(unconfirmed);

        await AssertCaptchaErrorAsync(forUnknown, HttpStatusCode.BadRequest, "captcha_invalid");
        await AssertCaptchaErrorAsync(forUnconfirmed, HttpStatusCode.BadRequest, "captcha_invalid");   // не 403 email_not_confirmed
        await AssertCaptchaErrorAsync(unconfirmedPassed, HttpStatusCode.Forbidden, "email_not_confirmed");
    }

    [Fact]
    public async Task Login_without_a_token_is_400_captcha_required()
    {
        var api = NewClient();
        var before = Calls;

        var response = await api.PostAsync("/auth/login", new { email = AuthApi.NewEmail(), password = AuthApi.Password });

        await AssertCaptchaErrorAsync(response, HttpStatusCode.BadRequest, "captcha_required");
        Assert.Equal(before, Calls);
    }

    // ---------- forgot-password ----------

    [Fact]
    public async Task Forgot_password_with_a_passed_captcha_sends_the_mail()
    {
        var email = await NewClient().RegisterConfirmedUserAsync();
        var api = NewClient();

        var response = await api.ForgotPasswordAsync(email);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(_factory.Mail.To(email), m => m.Body.Contains("reset-password"));
    }

    [Fact]
    public async Task Forgot_password_without_a_passed_captcha_is_400_and_sends_nothing()
    {
        var email = await NewClient().RegisterConfirmedUserAsync();
        var api = NewClient();
        var mailsBefore = _factory.Mail.To(email).Count;

        var rejected = await api.ForgotPasswordAsync(email, captchaToken: "bot-token");
        var missing = await api.PostAsync("/auth/forgot-password", new { email });

        await AssertCaptchaErrorAsync(rejected, HttpStatusCode.BadRequest, "captcha_invalid");
        await AssertCaptchaErrorAsync(missing, HttpStatusCode.BadRequest, "captcha_required");
        Assert.Equal(mailsBefore, _factory.Mail.To(email).Count);
    }

    [Fact]
    public async Task Forgot_password_still_answers_the_same_for_known_and_unknown_emails_once_the_captcha_is_passed()
    {
        var known = await NewClient().RegisterConfirmedUserAsync();
        var api = NewClient();

        var a = await api.ForgotPasswordAsync(known);
        var b = await api.ForgotPasswordAsync(AuthApi.NewEmail());

        Assert.Equal(HttpStatusCode.NoContent, a.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, b.StatusCode);
    }

    // ---------- порядок защит: IP-лимит → капча → лимит по email ----------

    [Fact]
    public async Task Failed_captchas_do_not_use_up_the_per_email_mail_quota()
    {
        var email = await NewClient().RegisterConfirmedUserAsync();
        var api = NewClient();

        // Бот шлёт мусор на чужой адрес: капча его не пускает и квоту «3 письма в час» на этот адрес не тратит.
        for (var i = 0; i < 5; i++)
        {
            await AssertCaptchaErrorAsync(
                await api.ForgotPasswordAsync(email, captchaToken: "bot-token"), HttpStatusCode.BadRequest, "captcha_invalid");
        }

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await api.ForgotPasswordAsync(email)).StatusCode);   // настоящему владельцу доступны все три
        }

        await RateLimitAssert.RateLimitedAsync(await api.ForgotPasswordAsync(email), 3600);   // а четвёртое — уже лимит
    }

    [Fact]
    public async Task The_ip_limit_is_applied_before_the_captcha_so_cloudflare_is_not_called_for_limited_requests()
    {
        var api = NewClient();
        var before = Calls;

        for (var i = 0; i < 10; i++)
        {
            await AssertCaptchaErrorAsync(await api.LoginAsync(AuthApi.NewEmail(), captchaToken: "bot-token"), HttpStatusCode.BadRequest, "captcha_invalid");
        }

        var eleventh = await api.LoginAsync(AuthApi.NewEmail(), captchaToken: "bot-token");

        await RateLimitAssert.RateLimitedAsync(eleventh, 60);
        Assert.Equal(before + 10, Calls);   // десять обращений в Cloudflare, а на одиннадцатое лимит не пустил
    }

    // ---------- сбои Cloudflare: закрыто, а не «пропустить» ----------

    [Theory]
    [InlineData("outage")]
    [InlineData("http-500")]
    [InlineData("garbage")]
    [InlineData("slow")]
    [InlineData("wrong-secret")]
    [InlineData("internal")]
    public async Task When_cloudflare_cannot_be_asked_register_is_503_and_creates_nothing(string failure)
    {
        var api = NewClient();
        var email = AuthApi.NewEmail();

        var response = await api.RegisterAsync(email, captchaToken: failure);

        await AssertCaptchaErrorAsync(response, HttpStatusCode.ServiceUnavailable, "captcha_unavailable");
        Assert.False(await UserExistsAsync(email));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ApiFactory.TurnstileTestSecret, body);   // секрет клиенту не показываем
        Assert.DoesNotContain(failure, body);                         // и чужой токен в ответ не отражаем
    }

    [Theory]
    [InlineData("outage")]
    [InlineData("slow")]
    [InlineData("wrong-secret")]
    public async Task When_cloudflare_cannot_be_asked_login_and_forgot_password_are_503_too(string failure)
    {
        var email = await NewClient().RegisterConfirmedUserAsync();
        var api = NewClient();
        var mailsBefore = _factory.Mail.To(email).Count;

        var login = await api.LoginAsync(email, captchaToken: failure);
        var forgot = await api.ForgotPasswordAsync(email, captchaToken: failure);

        await AssertCaptchaErrorAsync(login, HttpStatusCode.ServiceUnavailable, "captcha_unavailable");
        await AssertCaptchaErrorAsync(forgot, HttpStatusCode.ServiceUnavailable, "captcha_unavailable");
        Assert.Null(AuthApi.SetCookieHeader(login));                       // сбой капчи не открывает вход
        Assert.Equal(mailsBefore, _factory.Mail.To(email).Count);
    }

    [Fact]
    public async Task An_unknown_cloudflare_error_code_is_treated_as_a_bad_token_so_the_user_can_retry()
    {
        var response = await NewClient().RegisterAsync(AuthApi.NewEmail(), captchaToken: "unknown-code");

        await AssertCaptchaErrorAsync(response, HttpStatusCode.BadRequest, "captcha_invalid");
    }

    [Fact]
    public async Task After_a_cloudflare_outage_the_next_request_with_a_good_token_works()
    {
        var api = NewClient();

        await api.RegisterAsync(AuthApi.NewEmail(), captchaToken: "outage");
        var recovered = await api.RegisterAsync(AuthApi.NewEmail());

        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);   // сбой не «залипает»
    }

    // ---------- что капчей не защищено ----------

    [Fact]
    public async Task The_other_endpoints_do_not_ask_for_a_captcha_and_never_call_cloudflare()
    {
        var api = new AuthApi(_factory, TestIps.Next());   // без «решателя»: токена нет вообще
        var email = AuthApi.NewEmail();
        var before = Calls;

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.RefreshAsync("never-issued")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.ResetPasswordAsync(Guid.NewGuid().ToString(), "x", "newpass123")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.ConfirmEmailCodeAsync(AuthApi.NewEmail(), "123456")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.ResendConfirmationAsync(email)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.LoginTwoFactorAsync("garbage", "123456")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.TwoFactorSetupAsync(null, "garbage")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.MeAsync(null)).StatusCode);

        Assert.Equal(before, Calls);
    }
}
