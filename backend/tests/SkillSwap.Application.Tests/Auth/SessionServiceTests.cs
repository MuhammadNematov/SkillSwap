using SkillSwap.Application.Auth;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Tests.Auth;

public class SessionServiceTests
{
    private readonly AuthFixture _fx = new();
    private readonly Guid _userId = Guid.NewGuid();

    private Guid AddSession(Guid? userId = null, int daysToLive = 30, string device = "Firefox", bool revoked = false)
    {
        var now = _fx.Clock.GetUtcNow().UtcDateTime;
        var sessionId = Guid.NewGuid();
        var token = RefreshToken.Create(
            userId ?? _userId, Guid.NewGuid().ToString("N"), now.AddDays(daysToLive), device, "10.0.0.1", sessionId, now);
        _fx.RefreshTokens.Add(token);
        if (revoked)
        {
            token.Revoke(now);
        }

        return sessionId;
    }

    // ---------- список ----------

    [Fact]
    public async Task List_returns_the_users_active_sessions_with_their_details()
    {
        var id = AddSession(device: "Chrome on Windows");

        var sessions = await _fx.Sessions.ListAsync(_userId, null);

        var s = Assert.Single(sessions);
        Assert.Equal(id, s.Id);
        Assert.Equal("Chrome on Windows", s.DeviceInfo);
        Assert.Equal("10.0.0.1", s.IpAddress);
        Assert.Equal(_fx.Clock.GetUtcNow().UtcDateTime, s.StartedAt);
        Assert.Equal(_fx.Clock.GetUtcNow().UtcDateTime.AddDays(30), s.ExpiresAt);
    }

    [Fact]
    public async Task List_gives_a_readable_device_name_and_type_and_keeps_the_raw_agent_for_debugging()
    {
        AddSession(device: "phone-agent");

        var s = Assert.Single(await _fx.Sessions.ListAsync(_userId, null));

        Assert.Equal("устройство:phone-agent", s.DeviceName);
        Assert.Equal(DeviceType.Phone, s.DeviceType);
        Assert.Equal("phone-agent", s.DeviceInfo);
    }

    [Fact]
    public async Task A_session_without_a_user_agent_has_no_device_name()
    {
        AddSession(device: "");

        var s = Assert.Single(await _fx.Sessions.ListAsync(_userId, null));

        Assert.Null(s.DeviceName);
        Assert.Equal(DeviceType.Unknown, s.DeviceType);
    }

    [Fact]
    public async Task List_marks_only_the_current_session()
    {
        var first = AddSession();
        var second = AddSession();

        var sessions = await _fx.Sessions.ListAsync(_userId, second);

        Assert.Equal(second, Assert.Single(sessions, s => s.IsCurrent).Id);
        Assert.False(sessions.Single(s => s.Id == first).IsCurrent);
    }

    [Fact]
    public async Task List_without_a_current_session_marks_none()
    {
        AddSession();

        var sessions = await _fx.Sessions.ListAsync(_userId, null);

        Assert.All(sessions, s => Assert.False(s.IsCurrent));
    }

    [Fact]
    public async Task List_hides_revoked_expired_and_foreign_sessions()
    {
        var alive = AddSession();
        AddSession(revoked: true);
        AddSession(daysToLive: -1);                 // истёк
        AddSession(userId: Guid.NewGuid());          // чужой

        var sessions = await _fx.Sessions.ListAsync(_userId, null);

        Assert.Equal(alive, Assert.Single(sessions).Id);
    }

    [Fact]
    public async Task List_shows_the_newest_activity_first()
    {
        var older = AddSession();
        _fx.Clock.Advance(TimeSpan.FromMinutes(10));
        var newer = AddSession();

        var sessions = await _fx.Sessions.ListAsync(_userId, null);

        Assert.Equal([newer, older], sessions.Select(s => s.Id));
    }

    [Fact]
    public async Task List_shows_one_row_per_session_even_after_rotations()
    {
        var user = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "en");
        user.EmailConfirmed = true;
        _fx.Users.Existing = user;
        var login = await _fx.Service.LoginAsync(new LoginRequest("ali@example.com", "abc12345"), new ClientInfo("Firefox", "1.1.1.1"));
        _fx.Clock.Advance(TimeSpan.FromHours(1));
        var refreshed = await _fx.Service.RefreshAsync(login.RefreshToken, new ClientInfo("Firefox", "2.2.2.2"));
        _fx.Clock.Advance(TimeSpan.FromHours(1));
        await _fx.Service.RefreshAsync(refreshed.RefreshToken, new ClientInfo("Firefox", "3.3.3.3"));

        var sessions = await _fx.Sessions.ListAsync(user.Id, null);

        var s = Assert.Single(sessions);
        Assert.Equal("3.3.3.3", s.IpAddress);                                              // последний адрес
        Assert.Equal(new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), s.StartedAt);   // начало — момент входа
        Assert.Equal(new DateTime(2026, 6, 1, 14, 0, 0, DateTimeKind.Utc), s.LastActiveAt); // последняя активность — последнее обновление
        Assert.Equal(3, _fx.RefreshTokens.Tokens.Count);                                     // а токенов в БД три
    }

    // ---------- завершение одного ----------

    [Fact]
    public async Task Revoke_ends_that_session_only()
    {
        var target = AddSession();
        var other = AddSession();

        await _fx.Sessions.RevokeAsync(_userId, target);

        var remaining = await _fx.Sessions.ListAsync(_userId, null);
        Assert.Equal(other, Assert.Single(remaining).Id);
    }

    [Fact]
    public async Task Revoke_of_a_foreign_session_is_404_and_leaves_it_alone()
    {
        var victim = Guid.NewGuid();
        var foreign = AddSession(userId: victim);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => _fx.Sessions.RevokeAsync(_userId, foreign));

        Assert.Equal("session_not_found", ex.Code);
        Assert.Single(await _fx.Sessions.ListAsync(victim, null));
    }

    [Fact]
    public async Task Revoke_of_an_unknown_or_already_ended_session_is_404()
    {
        var ended = AddSession(revoked: true);

        await Assert.ThrowsAsync<NotFoundException>(() => _fx.Sessions.RevokeAsync(_userId, Guid.NewGuid()));
        await Assert.ThrowsAsync<NotFoundException>(() => _fx.Sessions.RevokeAsync(_userId, ended));
    }

    [Fact]
    public async Task Revoke_twice_is_404_the_second_time()
    {
        var id = AddSession();
        await _fx.Sessions.RevokeAsync(_userId, id);

        await Assert.ThrowsAsync<NotFoundException>(() => _fx.Sessions.RevokeAsync(_userId, id));
    }

    // ---------- завершение остальных ----------

    [Fact]
    public async Task RevokeOthers_keeps_the_current_session_and_ends_the_rest()
    {
        var current = AddSession();
        AddSession();
        AddSession();

        var count = await _fx.Sessions.RevokeOthersAsync(_userId, current);

        Assert.Equal(2, count);
        Assert.Equal(current, Assert.Single(await _fx.Sessions.ListAsync(_userId, current)).Id);
    }

    [Fact]
    public async Task RevokeOthers_does_not_touch_other_users()
    {
        var current = AddSession();
        var foreignUser = Guid.NewGuid();
        AddSession(userId: foreignUser);

        await _fx.Sessions.RevokeOthersAsync(_userId, current);

        Assert.Single(await _fx.Sessions.ListAsync(foreignUser, null));
    }

    [Fact]
    public async Task RevokeOthers_with_only_the_current_session_ends_nothing()
    {
        var current = AddSession();

        Assert.Equal(0, await _fx.Sessions.RevokeOthersAsync(_userId, current));
        Assert.Single(await _fx.Sessions.ListAsync(_userId, current));
    }

    [Fact]
    public async Task RevokeOthers_without_a_known_current_session_is_refused_instead_of_ending_everything()
    {
        AddSession();
        AddSession();

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Sessions.RevokeOthersAsync(_userId, null));

        Assert.Equal("invalid_session", ex.Code);
        Assert.Equal(2, (await _fx.Sessions.ListAsync(_userId, null)).Count);   // ничего не завершено
    }
}
