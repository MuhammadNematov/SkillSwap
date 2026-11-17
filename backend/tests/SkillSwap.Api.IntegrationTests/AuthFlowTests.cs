using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>
/// Весь путь пользователя через настоящий HTTP и настоящий PostgreSQL:
/// регистрация → подтверждение email → вход → /me → refresh с ротацией → повторное использование токена.
/// </summary>
[Collection(ApiCollection.Name)]
public class AuthFlowTests
{
    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public AuthFlowTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    // ---------- регистрация ----------

    [Fact]
    public async Task Register_gives_201_and_sends_a_confirmation_link_in_the_language_from_Accept_Language()
    {
        var email = AuthApi.NewEmail();

        var response = await _api.RegisterAsync(email, acceptLanguage: "ru-RU,ru;q=0.9,en;q=0.8");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var mail = Assert.Single(_factory.Mail.To(email));
        Assert.Contains("подтвердите", mail.Subject);
        var (userId, token) = _factory.Mail.LastConfirmationLink(email);
        Assert.NotEmpty(userId);
        Assert.NotEmpty(token);

        var user = await FindUserAsync(email);
        Assert.Equal("ru", user.UiLanguage);
        Assert.False(user.EmailConfirmed);
        Assert.NotEqual(AuthApi.Password, user.PasswordHash);   // пароль хранится хешем
    }

    [Fact]
    public async Task Register_with_the_same_email_gives_409()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();

        var response = await _api.RegisterAsync(email);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("email_already_registered", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task Register_with_invalid_data_gives_400_with_error_codes_per_field()
    {
        var response = await _api.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "not-an-email",
            password = "abc",
            name = "",
            dateOfBirth = "2015-01-01",
            acceptTerms = false,
            acceptChatModeration = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", json.GetProperty("code").GetString());
        var errors = json.GetProperty("errors");
        Assert.Equal("invalid_email", errors.GetProperty("email")[0].GetString());
        Assert.Equal("too_young", errors.GetProperty("dateOfBirth")[0].GetString());
        Assert.Equal("terms_not_accepted", errors.GetProperty("acceptTerms")[0].GetString());
    }

    // ---------- подтверждение email и вход ----------

    [Fact]
    public async Task Login_before_email_confirmation_gives_403()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();

        var response = await _api.LoginAsync(email);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("email_not_confirmed", await AuthApi.ReadCodeAsync(response));
        Assert.Null(AuthApi.SetCookieHeader(response));
    }

    [Fact]
    public async Task Confirm_email_with_a_wrong_token_gives_400_and_the_user_stays_unconfirmed()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();
        var (userId, _) = _factory.Mail.LastConfirmationLink(email);

        var response = await _api.ConfirmEmailAsync(userId, "wrong-token");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_confirmation_token", await AuthApi.ReadCodeAsync(response));
        Assert.False((await FindUserAsync(email)).EmailConfirmed);
    }

    [Fact]
    public async Task Confirm_email_then_login_returns_an_access_token_and_a_secure_refresh_cookie()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();
        var (userId, token) = _factory.Mail.LastConfirmationLink(email);

        var confirm = await _api.ConfirmEmailAsync(userId, token);
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        Assert.True((await FindUserAsync(email)).EmailConfirmed);

        var login = await _api.LoginAsync(email);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.Equal("User", body.GetProperty("user").GetProperty("role").GetString());   // enum — строкой
        Assert.False(body.TryGetProperty("refreshToken", out _));                          // refresh не в теле

        var cookie = AuthApi.SetCookieHeader(login);
        Assert.NotNull(cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", cookie, StringComparison.OrdinalIgnoreCase);

        // В БД лежит хеш токена, а не сам токен.
        var raw = AuthApi.RefreshTokenFrom(login)!;
        var stored = await _factory.QueryAsync(db => db.Set<RefreshToken>().SingleAsync(t => t.TokenHash == AuthApi.Sha256Hex(raw)));
        Assert.NotEqual(raw, stored.TokenHash);
        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task Login_with_a_wrong_password_gives_401()
    {
        var email = await _api.RegisterConfirmedUserAsync();

        var response = await _api.LoginAsync(email, "wrong-pass1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var email = await _api.RegisterConfirmedUserAsync();

        for (var i = 0; i < 5; i++)
        {
            var wrong = await _api.LoginAsync(email, "wrong-pass1");
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var locked = await _api.LoginAsync(email);   // пароль верный, но аккаунт заблокирован на 15 минут

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("account_locked_out", await AuthApi.ReadCodeAsync(locked));
    }

    // ---------- /me ----------

    [Fact]
    public async Task Me_without_a_token_or_with_a_garbage_token_gives_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.MeAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.MeAsync("garbage")).StatusCode);
    }

    [Fact]
    public async Task Me_with_a_valid_access_token_returns_the_user()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (access, _) = await _api.LoginTokensAsync(email);

        var response = await _api.MeAsync(access);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(email, json.GetProperty("email").GetString());
        Assert.Equal("User", json.GetProperty("role").GetString());
    }

    // ---------- refresh ----------

    [Fact]
    public async Task Refresh_rotates_the_token_and_returns_a_new_access_token()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (_, refresh1) = await _api.LoginTokensAsync(email);

        var response = await _api.RefreshAsync(refresh1);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refresh2 = AuthApi.RefreshTokenFrom(response);
        Assert.NotNull(refresh2);
        Assert.NotEqual(refresh1, refresh2);

        var newAccess = await AuthApi.ReadAccessTokenAsync(response);
        Assert.Equal(HttpStatusCode.OK, (await _api.MeAsync(newAccess)).StatusCode);

        var old = await _factory.QueryAsync(db => db.Set<RefreshToken>().SingleAsync(t => t.TokenHash == AuthApi.Sha256Hex(refresh1)));
        Assert.NotNull(old.RevokedAt);
        Assert.Equal(AuthApi.Sha256Hex(refresh2!), old.ReplacedByTokenHash);
    }

    [Fact]
    public async Task Reusing_an_old_refresh_token_gives_refresh_token_reused_and_revokes_every_session()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (_, refresh1) = await _api.LoginTokensAsync(email);
        var refresh2 = AuthApi.RefreshTokenFrom(await _api.RefreshAsync(refresh1))!;
        var otherDevice = (await _api.LoginTokensAsync(email)).RefreshToken;   // второй сеанс того же пользователя

        var reuse = await _api.RefreshAsync(refresh1);   // кто-то предъявил уже заменённый токен

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal("refresh_token_reused", await AuthApi.ReadCodeAsync(reuse));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RefreshAsync(refresh2)).StatusCode);       // новый токен отозван
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RefreshAsync(otherDevice)).StatusCode);    // и другие сеансы тоже

        var userId = (await FindUserAsync(email)).Id;
        var active = await _factory.QueryAsync(db => db.Set<RefreshToken>().CountAsync(t => t.UserId == userId && t.RevokedAt == null));
        Assert.Equal(0, active);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("never-issued")]
    public async Task Refresh_without_or_with_an_unknown_cookie_gives_401(string? cookie)
    {
        var response = await _api.RefreshAsync(cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_refresh_token", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task Two_parallel_refreshes_with_the_same_token_let_only_one_through()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (_, refresh) = await _api.LoginTokensAsync(email);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => _api.RefreshAsync(refresh)));

        // Победитель — 200. Проигравший: 409 (конфликт по xmin) или 401 (токен уже отозван).
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.Unauthorized }));
    }

    // ---------- logout ----------

    [Fact]
    public async Task Logout_revokes_the_refresh_token_and_clears_the_cookie()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (_, refresh) = await _api.LoginTokensAsync(email);

        var logout = await _api.LogoutAsync(refresh);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(string.Empty, AuthApi.RefreshTokenFrom(logout));   // cookie очищена
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RefreshAsync(refresh)).StatusCode);
    }

    // ---------- повторная отправка письма ----------

    [Fact]
    public async Task Resend_confirmation_gives_204_for_known_and_unknown_emails_but_sends_mail_only_to_the_unconfirmed()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();
        var unknown = AuthApi.NewEmail();

        var known = await _api.Client.PostAsJsonAsync("/api/v1/auth/resend-confirmation", new { email });
        var other = await _api.Client.PostAsJsonAsync("/api/v1/auth/resend-confirmation", new { email = unknown });

        Assert.Equal(HttpStatusCode.NoContent, known.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);   // не раскрываем, кто зарегистрирован
        Assert.Equal(2, _factory.Mail.To(email).Count);
        Assert.Empty(_factory.Mail.To(unknown));
    }

    private Task<User> FindUserAsync(string email) =>
        _factory.QueryAsync(db => db.Set<User>().SingleAsync(u => u.Email == email));
}
