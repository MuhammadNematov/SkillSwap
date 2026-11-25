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
        _fx.Clock.Advance(TimeSpan.FromSeconds(16));   // льготное окно (15 с) прошло

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
        _fx.Clock.Advance(TimeSpan.FromSeconds(16));   // льготное окно прошло

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Service.RefreshAsync(laptop.RefreshToken, Client));

        Assert.Equal("refresh_token_reused", ex.Code);
        await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Service.RefreshAsync(phone.RefreshToken, Client));   // телефон тоже отозван
    }

    // ---------- льготное окно после ротации (Auth:RefreshGraceSeconds) ----------

    [Fact]
    public async Task A_repeat_with_the_old_token_inside_the_grace_window_returns_the_same_successor_and_keeps_the_session()
    {
        AddUser();
        var first = await Login();
        var second = await _fx.Service.RefreshAsync(first.RefreshToken, Client);
        _fx.Clock.Advance(TimeSpan.FromSeconds(5));

        var repeat = await _fx.Service.RefreshAsync(first.RefreshToken, Client);   // вкладка не получила ответ и повторила

        Assert.Equal(second.RefreshToken, repeat.RefreshToken);                    // тот же токен, не новый
        Assert.Equal(second.RefreshTokenExpiresAt, repeat.RefreshTokenExpiresAt);
        Assert.Equal(2, _fx.RefreshTokens.Tokens.Count);                           // цепочка не ветвится
        Assert.DoesNotContain(_fx.RefreshTokens.Tokens, t => t.IsRevoked && t.TokenHash == AuthTokenService.Hash(second.RefreshToken));
        var next = await _fx.Service.RefreshAsync(repeat.RefreshToken, Client);    // и сеанс жив: преемник продолжает работать
        Assert.NotEqual(second.RefreshToken, next.RefreshToken);
    }

    [Fact]
    public async Task Several_repeats_inside_the_window_all_get_the_same_token_and_each_use_is_audited()
    {
        var user = AddUser();
        var first = await Login();
        var second = await _fx.Service.RefreshAsync(first.RefreshToken, Client);

        var a = await _fx.Service.RefreshAsync(first.RefreshToken, Client);
        _fx.Clock.Advance(TimeSpan.FromSeconds(10));
        var b = await _fx.Service.RefreshAsync(first.RefreshToken, Client);

        Assert.Equal(second.RefreshToken, a.RefreshToken);
        Assert.Equal(second.RefreshToken, b.RefreshToken);
        Assert.Equal(2, _fx.RefreshTokens.Tokens.Count);   // вход и одна ротация: ветвления нет
        Assert.Equal(2, _fx.Audit.Entries.Count(e => e.Action == "RefreshGraceUsed" && e.ActorId == user.Id && e.TargetType == "Session"));
    }

    [Fact]
    public async Task A_repeat_after_the_grace_window_is_reuse_and_revokes_the_sessions()
    {
        AddUser();
        var first = await Login();
        await _fx.Service.RefreshAsync(first.RefreshToken, Client);
        _fx.Clock.Advance(TimeSpan.FromSeconds(15) + TimeSpan.FromMilliseconds(1));

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Service.RefreshAsync(first.RefreshToken, Client));

        Assert.Equal("refresh_token_reused", ex.Code);
        Assert.All(_fx.RefreshTokens.Tokens, t => Assert.True(t.IsRevoked));
        Assert.Empty(_fx.Audit.Entries);   // отказ не выдаёт льготный токен
    }

    [Fact]
    public async Task The_window_boundary_is_inclusive_at_exactly_the_configured_seconds()
    {
        AddUser();
        var first = await Login();
        var second = await _fx.Service.RefreshAsync(first.RefreshToken, Client);
        _fx.Clock.Advance(TimeSpan.FromSeconds(15));

        Assert.Equal(second.RefreshToken, (await _fx.Service.RefreshAsync(first.RefreshToken, Client)).RefreshToken);
    }

    [Fact]
    public async Task A_repeat_inside_the_window_after_the_successor_was_already_used_is_reuse()
    {
        AddUser();
        var phone = await Login();   // второй сеанс: он тоже должен быть отозван
        var first = await Login();
        var second = await _fx.Service.RefreshAsync(first.RefreshToken, Client);
        await _fx.Service.RefreshAsync(second.RefreshToken, Client);   // преемник сам использован (ротация дальше)
        _fx.Clock.Advance(TimeSpan.FromSeconds(3));                      // окно ещё не закончилось

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Service.RefreshAsync(first.RefreshToken, Client));

        Assert.Equal("refresh_token_reused", ex.Code);
        Assert.All(_fx.RefreshTokens.Tokens, t => Assert.True(t.IsRevoked));
        await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Service.RefreshAsync(phone.RefreshToken, Client));
        Assert.Empty(_fx.Audit.Entries);
    }

    [Fact]
    public async Task A_repeat_inside_the_window_after_logout_of_the_successor_is_just_invalid_not_reuse()
    {
        AddUser();
        var other = await Login();
        var first = await Login();
        var second = await _fx.Service.RefreshAsync(first.RefreshToken, Client);
        await _fx.Service.LogoutAsync(second.RefreshToken);   // вышли

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => _fx.Service.RefreshAsync(first.RefreshToken, Client));

        Assert.Equal("invalid_refresh_token", ex.Code);
        await _fx.Service.RefreshAsync(other.RefreshToken, Client);   // чужой сеанс не тронут
    }

    [Fact]
    public async Task With_the_window_set_to_zero_any_repeat_is_reuse()
    {
        var fx = new AuthFixture(graceSeconds: 0);
        fx.Users.Existing = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "ru");
        fx.Users.Existing.EmailConfirmed = true;
        var login = await fx.Service.LoginAsync(new LoginRequest("ali@example.com", "abc12345"), Client);
        await fx.Service.RefreshAsync(login.RefreshToken!, Client);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => fx.Service.RefreshAsync(login.RefreshToken!, Client));

        Assert.Equal("refresh_token_reused", ex.Code);
    }

    [Fact]
    public async Task A_blocked_account_gets_no_token_from_the_window()
    {
        var user = AddUser();
        var first = await Login();
        await _fx.Service.RefreshAsync(first.RefreshToken, Client);
        user.Block();

        await Assert.ThrowsAnyAsync<Exception>(() => _fx.Service.RefreshAsync(first.RefreshToken, Client));
        Assert.All(_fx.RefreshTokens.Tokens, t => Assert.True(t.IsRevoked));
    }

    [Fact]
    public async Task The_successor_token_is_an_HMAC_under_a_key_derived_by_HKDF_from_the_signing_key_not_under_the_signing_key_itself()
    {
        AddUser();
        var first = await Login();
        var second = await _fx.Service.RefreshAsync(first.RefreshToken, Client);

        var signingKey = System.Text.Encoding.UTF8.GetBytes("unit-test-signing-key-0123456789abcdef");
        var previousHash = System.Text.Encoding.UTF8.GetBytes(AuthTokenService.Hash(first.RefreshToken));
        var derived = System.Security.Cryptography.HKDF.DeriveKey(
            System.Security.Cryptography.HashAlgorithmName.SHA256, signingKey, 32, salt: null, info: System.Text.Encoding.UTF8.GetBytes(AuthTokenService.SuccessorKeyLabel));
        var expected = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.HMACSHA256.HashData(derived, previousHash));
        var underSigningKey = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.HMACSHA256.HashData(signingKey, previousHash));
        var otherLabel = System.Security.Cryptography.HKDF.DeriveKey(
            System.Security.Cryptography.HashAlgorithmName.SHA256, signingKey, 32, salt: null, info: System.Text.Encoding.UTF8.GetBytes("skillswap/other-purpose/v1"));

        Assert.Equal(expected, second.RefreshToken);                    // отдельный ключ из HKDF с меткой
        Assert.NotEqual(underSigningKey, second.RefreshToken);          // не ключ подписи JWT
        Assert.NotEqual(derived, otherLabel);                           // другая метка даёт другой ключ
        Assert.Equal(43, second.RefreshToken.Length);                   // 32 байта в base64url
    }
}
