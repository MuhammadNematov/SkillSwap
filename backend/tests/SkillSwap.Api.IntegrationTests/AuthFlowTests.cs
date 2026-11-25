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
        Assert.Contains("код подтверждения", mail.Subject);
        Assert.Matches(@"^\d{6}$", _factory.Mail.LastConfirmationCode(email));
        Assert.DoesNotContain("confirm-email", mail.Body);   // ссылки нет, только код

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

    [Theory]
    [InlineData("0007-05-05")]
    [InlineData("1899-12-31")]
    public async Task Register_with_a_birth_year_before_1900_gives_400_and_creates_nobody(string birth)
    {
        var email = AuthApi.NewEmail();

        var response = await _api.Client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = AuthApi.Password,
            name = "Test User",
            dateOfBirth = birth,
            acceptTerms = true,
            acceptChatModeration = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", json.GetProperty("code").GetString());
        Assert.Equal("date_too_early", json.GetProperty("errors").GetProperty("dateOfBirth")[0].GetString());
        Assert.Empty(_factory.Mail.To(email));   // письмо не ушло, пользователь не создан
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
    public async Task Confirm_email_with_a_wrong_code_gives_400_and_the_user_stays_unconfirmed()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();
        var right = _factory.Mail.LastConfirmationCode(email);

        var response = await _api.ConfirmEmailCodeAsync(email, right == "000000" ? "000001" : "000000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_confirmation_code", await AuthApi.ReadCodeAsync(response));
        Assert.False((await FindUserAsync(email)).EmailConfirmed);
    }

    [Fact]
    public async Task Confirm_email_then_login_returns_an_access_token_and_a_secure_refresh_cookie()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();
        var confirm = await _api.ConfirmEmailCodeAsync(email, _factory.Mail.LastConfirmationCode(email));
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
    public async Task A_successful_login_records_the_last_activity_time()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        Assert.Null((await FindUserAsync(email)).LastActiveAt);

        (await _api.LoginAsync(email)).EnsureSuccessStatusCode();

        var user = await FindUserAsync(email);
        Assert.NotNull(user.LastActiveAt);
        Assert.Equal(_factory.Clock.GetUtcNow().UtcDateTime, user.LastActiveAt!.Value, TimeSpan.FromMilliseconds(1));
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

    // ---------- льготное окно после ротации (Auth:RefreshGraceSeconds = 15) ----------

    [Fact]
    public async Task A_repeat_with_the_old_refresh_token_inside_the_grace_window_returns_the_same_token_and_the_session_survives()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (_, refresh1) = await _api.LoginTokensAsync(email);
        var first = await _api.RefreshAsync(refresh1);
        var refresh2 = AuthApi.RefreshTokenFrom(first)!;
        _factory.Clock.Advance(TimeSpan.FromSeconds(4));

        var repeat = await _api.RefreshAsync(refresh1);   // ответ первого запроса потерялся, вкладка повторила

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(refresh2, AuthApi.RefreshTokenFrom(repeat));                                              // тот же преемник
        Assert.Equal(HttpStatusCode.OK, (await _api.MeAsync(await AuthApi.ReadAccessTokenAsync(repeat))).StatusCode);

        var userId = (await FindUserAsync(email)).Id;
        var rows = await _factory.QueryAsync(db => db.Set<RefreshToken>().Where(t => t.UserId == userId).ToListAsync());
        Assert.Equal(1, rows.Count(t => t.RevokedAt == null));                                             // цепочка не ветвится: один действующий токен
        Assert.Equal(2, rows.Count);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(refresh2)).StatusCode);                       // и преемник работает

        var audit = await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.ActorId == userId && a.Action == "RefreshGraceUsed").ToListAsync());
        var entry = Assert.Single(audit);
        Assert.Equal("Session", entry.TargetType);
        Assert.Equal(rows[0].SessionId, entry.TargetId);
    }

    [Fact]
    public async Task A_repeat_inside_the_window_after_the_successor_was_used_revokes_the_session()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (_, refresh1) = await _api.LoginTokensAsync(email);
        var refresh2 = AuthApi.RefreshTokenFrom(await _api.RefreshAsync(refresh1))!;
        var refresh3 = AuthApi.RefreshTokenFrom(await _api.RefreshAsync(refresh2))!;   // преемник использован
        _factory.Clock.Advance(TimeSpan.FromSeconds(2));

        var reuse = await _api.RefreshAsync(refresh1);

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal("refresh_token_reused", await AuthApi.ReadCodeAsync(reuse));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RefreshAsync(refresh3)).StatusCode);
        var userId = (await FindUserAsync(email)).Id;
        Assert.Equal(0, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.ActorId == userId && a.Action == "RefreshGraceUsed")));
    }

    [Fact]
    public async Task Two_parallel_refreshes_with_the_same_token_both_succeed_and_share_one_successor()
    {
        for (var round = 0; round < 5; round++)
        {
            var email = await _api.RegisterConfirmedUserAsync();
            var (_, refresh1) = await _api.LoginTokensAsync(email);

            var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _api.RefreshAsync(refresh1)));   // три вкладки одновременно

            Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var cookies = results.Select(AuthApi.RefreshTokenFrom).Distinct().ToList();
            Assert.Single(cookies);                                                                           // один и тот же преемник у всех
            var userId = (await FindUserAsync(email)).Id;
            var rows = await _factory.QueryAsync(db => db.Set<RefreshToken>().Where(t => t.UserId == userId).ToListAsync());
            Assert.Equal(2, rows.Count);                                                                      // вход и один преемник, ветвления нет
            Assert.Equal(1, rows.Count(t => t.RevokedAt == null));
            Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(cookies[0])).StatusCode);                // сеанс жив
        }
    }

    [Fact]
    public async Task Reusing_an_old_refresh_token_gives_refresh_token_reused_and_revokes_every_session()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (_, refresh1) = await _api.LoginTokensAsync(email);
        var refresh2 = AuthApi.RefreshTokenFrom(await _api.RefreshAsync(refresh1))!;
        var otherDevice = (await _api.LoginTokensAsync(email)).RefreshToken;   // второй сеанс того же пользователя
        _factory.Clock.Advance(TimeSpan.FromSeconds(16));                       // льготное окно (15 с) прошло

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
