using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>
/// Список активных сеансов и их завершение (раздел 10.1 ТЗ) на настоящей БД:
/// GET /auth/sessions, DELETE /auth/sessions/{id}, DELETE /auth/sessions (кроме текущего).
/// </summary>
[Collection(ApiCollection.Name)]
public class SessionsFlowTests
{
    private const string LaptopUa = "TestBrowser/1.0 (Laptop)";
    private const string PhoneUa = "TestBrowser/1.0 (Phone)";

    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public SessionsFlowTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    private static string Id(JsonElement session) => session.GetProperty("id").GetString()!;

    // ---------- доступ ----------

    [Fact]
    public async Task All_three_endpoints_require_a_token()
    {
        var id = Guid.NewGuid().ToString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.ListSessionsAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RevokeSessionAsync(null, id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RevokeOtherSessionsAsync(null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.ListSessionsAsync("garbage")).StatusCode);
    }

    // ---------- список ----------

    [Fact]
    public async Task The_list_shows_every_device_with_its_details_and_marks_the_current_one()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var laptop = await _api.LoginSessionAsync(email, LaptopUa);
        var phone = await _api.LoginSessionAsync(email, PhoneUa);

        var response = await _api.ListSessionsAsync(laptop.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sessions = await AuthApi.ReadListAsync(response);
        Assert.Equal(2, sessions.Count);

        var mine = sessions.Single(s => Id(s) == laptop.SessionId);
        var other = sessions.Single(s => Id(s) == phone.SessionId);
        Assert.True(mine.GetProperty("isCurrent").GetBoolean());
        Assert.False(other.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(LaptopUa, mine.GetProperty("deviceInfo").GetString());
        Assert.Equal(PhoneUa, other.GetProperty("deviceInfo").GetString());
        Assert.False(string.IsNullOrEmpty(mine.GetProperty("ipAddress").GetString()));
    }

    [Fact]
    public async Task The_list_has_login_time_last_activity_and_expiry()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var session = await _api.LoginSessionAsync(email, LaptopUa);

        var entry = Assert.Single(await AuthApi.ReadListAsync(await _api.ListSessionsAsync(session.AccessToken)));

        var started = entry.GetProperty("startedAt").GetDateTime();
        var lastActive = entry.GetProperty("lastActiveAt").GetDateTime();
        var expires = entry.GetProperty("expiresAt").GetDateTime();
        Assert.Equal(_factory.Clock.GetUtcNow().UtcDateTime, started, TimeSpan.FromSeconds(1));
        Assert.True(lastActive >= started);
        Assert.Equal(started.AddDays(30), expires, TimeSpan.FromSeconds(1));   // refresh-токен живёт 30 дней
        Assert.Equal(DateTimeKind.Utc, started.Kind);                          // все даты в UTC
    }

    [Fact]
    public async Task The_list_shows_the_ip_of_the_last_refresh()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var session = await _api.LoginSessionAsync(email, LaptopUa, remoteIp: "198.51.100.10");
        Assert.Equal("198.51.100.10", (await IpOfAsync(session)));

        // Тот же сеанс обновляет токен уже с другого адреса (например, ноутбук перешёл в другую сеть).
        var refreshed = await _api.RefreshAsync(session.RefreshToken, remoteIp: "198.51.100.99");
        var access = await AuthApi.ReadAccessTokenAsync(refreshed);

        var entry = Assert.Single(await AuthApi.ReadListAsync(await _api.ListSessionsAsync(access)));
        Assert.Equal("198.51.100.99", entry.GetProperty("ipAddress").GetString());
    }

    private async Task<string?> IpOfAsync(TestSession session)
    {
        var sessions = await AuthApi.ReadListAsync(await _api.ListSessionsAsync(session.AccessToken));
        return sessions.Single(s => Id(s) == session.SessionId).GetProperty("ipAddress").GetString();
    }

    [Fact]
    public async Task The_newest_session_is_listed_first()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var first = await _api.LoginSessionAsync(email, LaptopUa);
        var second = await _api.LoginSessionAsync(email, PhoneUa);

        var sessions = await AuthApi.ReadListAsync(await _api.ListSessionsAsync(first.AccessToken));

        Assert.Equal([second.SessionId, first.SessionId], sessions.Select(Id));
    }

    [Fact]
    public async Task A_session_keeps_its_id_and_stays_one_row_after_the_token_is_refreshed()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var session = await _api.LoginSessionAsync(email, LaptopUa);

        var refreshed = await _api.RefreshAsync(session.RefreshToken);
        var newAccess = await AuthApi.ReadAccessTokenAsync(refreshed);

        Assert.Equal(session.SessionId, AuthApi.SessionIdFrom(newAccess));   // sid не меняется при ротации
        var sessions = await AuthApi.ReadListAsync(await _api.ListSessionsAsync(newAccess));
        var entry = Assert.Single(sessions);                                  // а в БД уже два токена, но сеанс один
        Assert.Equal(session.SessionId, Id(entry));
        Assert.True(entry.GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task A_new_login_gets_a_new_session_id()
    {
        var email = await _api.RegisterConfirmedUserAsync();

        var a = await _api.LoginSessionAsync(email);
        var b = await _api.LoginSessionAsync(email);

        Assert.NotEqual(a.SessionId, b.SessionId);
    }

    [Fact]
    public async Task The_list_contains_only_my_sessions()
    {
        var mine = await _api.LoginSessionAsync(await _api.RegisterConfirmedUserAsync(), LaptopUa);
        await _api.LoginSessionAsync(await _api.RegisterConfirmedUserAsync(), PhoneUa);   // чужой сеанс

        var sessions = await AuthApi.ReadListAsync(await _api.ListSessionsAsync(mine.AccessToken));

        Assert.Equal(mine.SessionId, Id(Assert.Single(sessions)));
    }

    [Fact]
    public async Task A_logged_out_session_disappears_from_the_list()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var laptop = await _api.LoginSessionAsync(email, LaptopUa);
        var phone = await _api.LoginSessionAsync(email, PhoneUa);

        await _api.LogoutAsync(phone.RefreshToken);

        var sessions = await AuthApi.ReadListAsync(await _api.ListSessionsAsync(laptop.AccessToken));
        Assert.Equal(laptop.SessionId, Id(Assert.Single(sessions)));
    }

    // ---------- завершение одного ----------

    [Fact]
    public async Task Ending_another_session_logs_that_device_out_and_keeps_mine()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var laptop = await _api.LoginSessionAsync(email, LaptopUa);
        var phone = await _api.LoginSessionAsync(email, PhoneUa);

        var response = await _api.RevokeSessionAsync(laptop.AccessToken, phone.SessionId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var phoneRefresh = await _api.RefreshAsync(phone.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, phoneRefresh.StatusCode);
        Assert.Equal("invalid_refresh_token", await AuthApi.ReadCodeAsync(phoneRefresh));   // не refresh_token_reused
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(laptop.RefreshToken)).StatusCode);   // мой сеанс жив

        var left = await AuthApi.ReadListAsync(await _api.ListSessionsAsync(laptop.AccessToken));
        Assert.Equal(laptop.SessionId, Id(Assert.Single(left)));
    }

    [Fact]
    public async Task The_ended_device_trying_to_refresh_does_not_log_out_my_other_sessions()
    {
        // Регрессия: раньше любой отозванный токен считался «повторным использованием» и отзывал ВСЕ сеансы,
        // так что завершённое вручную устройство при следующей попытке обновиться разлогинивало остальные.
        var email = await _api.RegisterConfirmedUserAsync();
        var laptop = await _api.LoginSessionAsync(email, LaptopUa);
        var phone = await _api.LoginSessionAsync(email, PhoneUa);
        var tablet = await _api.LoginSessionAsync(email, "TestBrowser/1.0 (Tablet)");
        await _api.RevokeSessionAsync(laptop.AccessToken, phone.SessionId);

        await _api.RefreshAsync(phone.RefreshToken);   // телефон пытается обновиться

        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(laptop.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(tablet.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Ending_my_own_session_works_like_a_logout()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var laptop = await _api.LoginSessionAsync(email, LaptopUa);
        var phone = await _api.LoginSessionAsync(email, PhoneUa);

        var response = await _api.RevokeSessionAsync(laptop.AccessToken, laptop.SessionId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RefreshAsync(laptop.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(phone.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Ending_a_session_that_does_not_exist_gives_404()
    {
        var laptop = await _api.LoginSessionAsync(await _api.RegisterConfirmedUserAsync());

        var response = await _api.RevokeSessionAsync(laptop.AccessToken, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("session_not_found", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task Ending_another_users_session_gives_the_same_404_and_does_not_touch_it()
    {
        var attacker = await _api.LoginSessionAsync(await _api.RegisterConfirmedUserAsync());
        var victim = await _api.LoginSessionAsync(await _api.RegisterConfirmedUserAsync());

        var response = await _api.RevokeSessionAsync(attacker.AccessToken, victim.SessionId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("session_not_found", await AuthApi.ReadCodeAsync(response));   // неотличимо от несуществующего
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(victim.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Ending_the_same_session_twice_gives_404_the_second_time()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var laptop = await _api.LoginSessionAsync(email);
        var phone = await _api.LoginSessionAsync(email);
        await _api.RevokeSessionAsync(laptop.AccessToken, phone.SessionId);

        var second = await _api.RevokeSessionAsync(laptop.AccessToken, phone.SessionId);

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task A_malformed_session_id_gives_404()
    {
        var laptop = await _api.LoginSessionAsync(await _api.RegisterConfirmedUserAsync());

        var response = await _api.RevokeSessionAsync(laptop.AccessToken, "not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Two_parallel_requests_to_end_the_same_session_let_only_one_through()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var laptop = await _api.LoginSessionAsync(email);
        var phone = await _api.LoginSessionAsync(email);

        var results = await Task.WhenAll(
            _api.RevokeSessionAsync(laptop.AccessToken, phone.SessionId),
            _api.RevokeSessionAsync(laptop.AccessToken, phone.SessionId));

        var statuses = string.Join(", ", results.Select(r => (int)r.StatusCode));
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.NoContent) == 1, $"Статусы: {statuses}");
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.NotFound) == 1, $"Статусы: {statuses}");
    }

    // ---------- завершение остальных ----------

    [Fact]
    public async Task Ending_all_other_sessions_keeps_only_the_current_one()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var laptop = await _api.LoginSessionAsync(email, LaptopUa);
        var phone = await _api.LoginSessionAsync(email, PhoneUa);
        var tablet = await _api.LoginSessionAsync(email, "TestBrowser/1.0 (Tablet)");

        var response = await _api.RevokeOtherSessionsAsync(phone.AccessToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RefreshAsync(laptop.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RefreshAsync(tablet.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(phone.RefreshToken)).StatusCode);   // текущий жив

        var left = await AuthApi.ReadListAsync(await _api.ListSessionsAsync(phone.AccessToken));
        Assert.Equal(phone.SessionId, Id(Assert.Single(left)));
    }

    [Fact]
    public async Task Ending_all_other_sessions_with_only_one_session_is_a_harmless_204()
    {
        var only = await _api.LoginSessionAsync(await _api.RegisterConfirmedUserAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await _api.RevokeOtherSessionsAsync(only.AccessToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(only.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Ending_all_other_sessions_does_not_touch_other_users()
    {
        var mine = await _api.LoginSessionAsync(await _api.RegisterConfirmedUserAsync());
        var someoneElse = await _api.LoginSessionAsync(await _api.RegisterConfirmedUserAsync());

        await _api.RevokeOtherSessionsAsync(mine.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(someoneElse.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task The_session_survives_ending_others_even_after_it_was_rotated()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var laptop = await _api.LoginSessionAsync(email, LaptopUa);
        await _api.LoginSessionAsync(email, PhoneUa);
        var rotated = await _api.RefreshAsync(laptop.RefreshToken);   // у ноутбука теперь другой refresh-токен, тот же сеанс
        var newAccess = await AuthApi.ReadAccessTokenAsync(rotated);
        var newRefresh = AuthApi.RefreshTokenFrom(rotated)!;

        await _api.RevokeOtherSessionsAsync(newAccess);

        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(newRefresh)).StatusCode);
    }

    // ---------- что лежит в БД ----------

    [Fact]
    public async Task Ending_a_session_marks_its_token_revoked_without_a_replacement_link()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var laptop = await _api.LoginSessionAsync(email);
        var phone = await _api.LoginSessionAsync(email);
        var phoneSid = Guid.Parse(phone.SessionId);

        await _api.RevokeSessionAsync(laptop.AccessToken, phone.SessionId);

        var token = await _factory.QueryAsync(db => db.Set<RefreshToken>().SingleAsync(t => t.SessionId == phoneSid));
        Assert.NotNull(token.RevokedAt);
        Assert.Null(token.ReplacedByTokenHash);   // отозван вручную, а не заменён ротацией
    }

    [Fact]
    public async Task All_tokens_of_one_session_share_the_session_id_and_start_time()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var session = await _api.LoginSessionAsync(email);
        var sid = Guid.Parse(session.SessionId);
        var second = await _api.RefreshAsync(session.RefreshToken);
        await _api.RefreshAsync(AuthApi.RefreshTokenFrom(second)!);

        var tokens = await _factory.QueryAsync(db => db.Set<RefreshToken>().Where(t => t.SessionId == sid).ToListAsync());

        Assert.Equal(3, tokens.Count);
        Assert.Single(tokens.Select(t => t.SessionStartedAt).Distinct());
        Assert.Single(tokens, t => t.RevokedAt == null);   // действующий только последний
    }

    [Fact]
    public async Task A_password_reset_ends_every_session_so_the_list_becomes_empty_after_a_new_login()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        await _api.LoginSessionAsync(email, LaptopUa);
        await _api.LoginSessionAsync(email, PhoneUa);
        (await _api.ForgotPasswordAsync(email)).EnsureSuccessStatusCode();
        var (id, token) = _factory.Mail.LastResetLink(email);
        (await _api.ResetPasswordAsync(id, token, "after-reset-pass-3")).EnsureSuccessStatusCode();

        var fresh = await _api.LoginSessionAsync(email, "TestBrowser/1.0 (After reset)", "after-reset-pass-3");

        var sessions = await AuthApi.ReadListAsync(await _api.ListSessionsAsync(fresh.AccessToken));
        Assert.Equal(fresh.SessionId, Id(Assert.Single(sessions)));   // старые два сеанса исчезли
    }
}
