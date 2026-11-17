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

    private Task<AuthResult> Login(string password = "abc12345") =>
        _fx.Service.LoginAsync(new LoginRequest("ali@example.com", password), Client);

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
        Assert.NotNull(user.LastActiveAt);
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
    public async Task Login_with_two_factor_enabled_is_refused_until_2FA_is_implemented()
    {
        var user = AddUser();
        user.TwoFactorEnabled = true;

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => Login());

        Assert.Equal("two_factor_required", ex.Code);
        Assert.Empty(_fx.RefreshTokens.Tokens);
    }

    [Fact]
    public async Task Login_to_a_staff_account_is_refused_without_2FA()
    {
        var user = AddUser();
        typeof(User).GetProperty(nameof(User.AccountType))!.SetValue(user, AccountType.Staff);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => Login());

        Assert.Equal("two_factor_required", ex.Code);
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
}
