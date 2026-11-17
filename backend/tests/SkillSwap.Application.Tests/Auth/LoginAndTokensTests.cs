using SkillSwap.Application.Auth;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Tests.Auth;

public class LoginAndTokensTests
{
    private static readonly ClientInfo Client = new("Firefox", "127.0.0.1");

    private readonly AuthFixture _fx = new();

    private User AddUser(bool confirmed = true)
    {
        var user = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "ru");
        user.EmailConfirmed = confirmed;
        _fx.Users.Existing = user;
        return user;
    }

    private Task<LoginResult> LoginRaw(string password = "abc12345") =>
        _fx.Service.LoginAsync(new LoginRequest("ali@example.com", password), Client);

    /// <summary>Вход, который должен завершиться выдачей токенов (без 2FA).</summary>
    private async Task<AuthResult> Login(string password = "abc12345")
    {
        var r = await LoginRaw(password);
        Assert.False(r.Response.Requires2fa);
        Assert.False(r.Response.Requires2faSetup);
        return new AuthResult(
            new AuthResponse(r.Response.AccessToken!, r.Response.AccessTokenExpiresAt!.Value, r.Response.User!),
            r.RefreshToken!,
            r.RefreshTokenExpiresAt!.Value);
    }

    // ---------- вход ----------

    [Fact]
    public async Task Login_returns_access_token_and_a_refresh_token_stored_only_as_hash()
    {
        var user = AddUser();

        var result = await Login();

        Assert.Equal($"access-for-{user.Id}", result.Response.AccessToken);
        Assert.Equal(user.Id, result.Response.User.Id);
        Assert.Equal("ru", result.Response.User.UiLanguage);

        var stored = Assert.Single(_fx.RefreshTokens.Tokens);
        Assert.NotEqual(result.RefreshToken, stored.TokenHash);   // в БД — не сам токен
        Assert.Equal(AuthTokenService.Hash(result.RefreshToken), stored.TokenHash);
        Assert.Equal(_fx.Clock.GetUtcNow().UtcDateTime.AddDays(30), stored.ExpiresAt);
        Assert.Equal(result.RefreshTokenExpiresAt, stored.ExpiresAt);
        Assert.Equal("Firefox", stored.DeviceInfo);
        Assert.Equal(1, _fx.Users.ResetFailedCalls);
        Assert.Equal(_fx.Clock.GetUtcNow().UtcDateTime, _fx.Activity.Touched[user.Id]);   // отметка активности записана
    }

    [Fact]
    public async Task Login_with_wrong_password_counts_a_failed_attempt()
    {
        AddUser();

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => Login("wrong-pass1"));

        Assert.Equal("invalid_credentials", ex.Code);
        Assert.Equal(1, _fx.Users.AccessFailedCalls);
        Assert.Empty(_fx.RefreshTokens.Tokens);
    }

    [Fact]
    public async Task Login_with_unknown_email_gives_the_same_error_as_wrong_password()
    {
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => Login());

        Assert.Equal("invalid_credentials", ex.Code);
    }

    [Fact]
    public async Task Login_when_locked_out_is_rejected_even_with_the_right_password()
    {
        AddUser();
        _fx.Users.LockedOut = true;

        var ex = await Assert.ThrowsAsync<TooManyRequestsException>(() => Login());

        Assert.Equal("account_locked_out", ex.Code);
        Assert.Equal(0, _fx.Users.AccessFailedCalls);
    }

    [Fact]
    public async Task Login_with_unconfirmed_email_is_forbidden_but_only_after_a_correct_password()
    {
        AddUser(confirmed: false);

        var wrong = await Assert.ThrowsAsync<UnauthorizedException>(() => Login("wrong-pass1"));
        var right = await Assert.ThrowsAsync<ForbiddenException>(() => Login());

        Assert.Equal("invalid_credentials", wrong.Code);
        Assert.Equal("email_not_confirmed", right.Code);
    }

    [Fact]
    public async Task Login_of_a_deleted_user_looks_like_an_unknown_one()
    {
        var user = AddUser();
        typeof(User).GetProperty(nameof(User.Status))!.SetValue(user, UserStatus.Deleted);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => Login());

        Assert.Equal("invalid_credentials", ex.Code);
    }

    [Fact]
    public async Task Login_of_a_blocked_user_is_forbidden()
    {
        var user = AddUser();
        typeof(User).GetProperty(nameof(User.Status))!.SetValue(user, UserStatus.Blocked);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => Login());

        Assert.Equal("account_blocked", ex.Code);
    }

    [Fact]
    public async Task Login_with_two_factor_enabled_asks_for_a_code_and_issues_no_tokens()
    {
        var user = AddUser();
        user.TwoFactorEnabled = true;

        var result = await LoginRaw();

        Assert.True(result.Response.Requires2fa);
        Assert.False(result.Response.Requires2faSetup);
        Assert.Equal($"challenge:Login:{user.Id}", result.Response.TwoFactorToken);
        Assert.Null(result.Response.AccessToken);
        Assert.Null(result.RefreshToken);
        Assert.Empty(_fx.RefreshTokens.Tokens);
    }

    [Fact]
    public async Task Login_to_a_staff_account_without_2FA_asks_to_set_it_up_first()
    {
        var user = AddUser();
        typeof(User).GetProperty(nameof(User.AccountType))!.SetValue(user, AccountType.Staff);

        var result = await LoginRaw();

        Assert.True(result.Response.Requires2faSetup);
        Assert.False(result.Response.Requires2fa);
        Assert.Equal($"challenge:Setup:{user.Id}", result.Response.TwoFactorToken);
        Assert.Null(result.Response.AccessToken);
        Assert.Empty(_fx.RefreshTokens.Tokens);
    }

    [Fact]
    public async Task Login_to_a_staff_account_with_2FA_asks_for_a_code()
    {
        var user = AddUser();
        typeof(User).GetProperty(nameof(User.AccountType))!.SetValue(user, AccountType.Staff);
        user.TwoFactorEnabled = true;

        var result = await LoginRaw();

        Assert.True(result.Response.Requires2fa);
        Assert.Equal($"challenge:Login:{user.Id}", result.Response.TwoFactorToken);
    }
    // ---------- refresh ----------

    [Fact]
    public async Task Refresh_rotates_the_token_and_revokes_the_old_one()
    {
        AddUser();
        var first = await Login();

        var second = await _fx.Service.RefreshAsync(first.RefreshToken, Client);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal(2, _fx.RefreshTokens.Tokens.Count);

        var old = _fx.RefreshTokens.Tokens.Single(t => t.TokenHash == AuthTokenService.Hash(first.RefreshToken));
        Assert.True(old.IsRevoked);
        Assert.Equal(AuthTokenService.Hash(second.RefreshToken), old.ReplacedByTokenHash);
        Assert.Equal(first.Response.User.Id, second.Response.User.Id);
    }

    [Fact]
    public async Task Reusing_an_old_refresh_token_revokes_all_sessions_of_the_user()
    {
        AddUser();
        var first = await Login();
        var second = await _fx.Service.RefreshAsync(first.RefreshToken, Client);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.Service.RefreshAsync(first.RefreshToken, Client));   // кто-то предъявил уже заменённый токен

        Assert.Equal("refresh_token_reused", ex.Code);
        Assert.All(_fx.RefreshTokens.Tokens, t => Assert.True(t.IsRevoked));

        // Новый токен тоже больше не работает: нужно войти заново.
        await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.Service.RefreshAsync(second.RefreshToken, Client));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("never-issued")]
    public async Task Refresh_with_missing_or_unknown_token_is_unauthorized(string? token)
    {
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Service.RefreshAsync(token, Client));

        Assert.Equal("invalid_refresh_token", ex.Code);
    }

    [Fact]
    public async Task Refresh_after_30_days_is_unauthorized()
    {
        AddUser();
        var login = await Login();

        _fx.Clock.Advance(TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.Service.RefreshAsync(login.RefreshToken, Client));
        Assert.Equal("invalid_refresh_token", ex.Code);
    }

    [Fact]
    public async Task Refresh_for_a_blocked_user_revokes_everything_and_is_forbidden()
    {
        var user = AddUser();
        var login = await Login();
        typeof(User).GetProperty(nameof(User.Status))!.SetValue(user, UserStatus.Blocked);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => _fx.Service.RefreshAsync(login.RefreshToken, Client));

        Assert.Equal("account_blocked", ex.Code);
        Assert.All(_fx.RefreshTokens.Tokens, t => Assert.True(t.IsRevoked));
    }

    // ---------- logout ----------

    [Fact]
    public async Task Logout_revokes_the_token_so_it_cannot_be_refreshed()
    {
        AddUser();
        var login = await Login();

        await _fx.Service.LogoutAsync(login.RefreshToken);

        Assert.True(Assert.Single(_fx.RefreshTokens.Tokens).IsRevoked);
        await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.Service.RefreshAsync(login.RefreshToken, Client));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("never-issued")]
    public async Task Logout_with_missing_or_unknown_token_does_nothing(string? token)
    {
        await _fx.Service.LogoutAsync(token);

        Assert.Empty(_fx.RefreshTokens.Tokens);
    }

    // ---------- сеансы и ротация ----------

    [Fact]
    public async Task Refresh_keeps_the_same_session_and_a_new_login_starts_a_new_one()
    {
        AddUser();
        var first = await Login();
        var refreshed = await _fx.Service.RefreshAsync(first.RefreshToken, Client);
        var secondDevice = await Login();

        var sids = _fx.AccessTokens.SessionIds;   // sid, с которыми выпускались access-токены: вход, обновление, второй вход
        Assert.Equal(3, sids.Count);
        Assert.Equal(sids[0], sids[1]);           // ротация продолжает тот же сеанс
        Assert.NotEqual(sids[0], sids[2]);        // новый вход — новый сеанс
        Assert.NotNull(refreshed);
        Assert.NotNull(secondDevice);

        var tokens = _fx.RefreshTokens.Tokens;
        Assert.Equal(tokens[0].SessionId, tokens[1].SessionId);
        Assert.Equal(tokens[0].SessionStartedAt, tokens[1].SessionStartedAt);
    }

    [Fact]
    public async Task A_token_of_a_session_ended_from_the_session_list_gives_a_plain_401_and_leaves_other_sessions_alone()
    {
        var user = AddUser();
        var laptop = await Login();
        var phone = await Login();
        var laptopSession = _fx.RefreshTokens.Tokens[0].SessionId;

        await _fx.Sessions.RevokeAsync(user.Id, laptopSession);   // телефон завершает сеанс ноутбука

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Service.RefreshAsync(laptop.RefreshToken, Client));
        Assert.Equal("invalid_refresh_token", ex.Code);            // не refresh_token_reused: это не кража
        Assert.False(_fx.RefreshTokens.Tokens[1].IsRevoked);       // сеанс телефона жив
        await _fx.Service.RefreshAsync(phone.RefreshToken, Client);   // и продолжает работать
    }

    [Fact]
    public async Task A_token_revoked_by_logout_gives_a_plain_401_and_leaves_other_sessions_alone()
    {
        AddUser();
        var laptop = await Login();
        var phone = await Login();
        await _fx.Service.LogoutAsync(laptop.RefreshToken);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Service.RefreshAsync(laptop.RefreshToken, Client));

        Assert.Equal("invalid_refresh_token", ex.Code);
        await _fx.Service.RefreshAsync(phone.RefreshToken, Client);
    }

    [Fact]
    public async Task Only_a_token_replaced_by_rotation_counts_as_reuse()
    {
        AddUser();
        var laptop = await Login();
        var phone = await Login();
        await _fx.Service.RefreshAsync(laptop.RefreshToken, Client);   // ротация: старый токен ноутбука заменён

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Service.RefreshAsync(laptop.RefreshToken, Client));

        Assert.Equal("refresh_token_reused", ex.Code);
        await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Service.RefreshAsync(phone.RefreshToken, Client));   // телефон тоже отозван
    }
}