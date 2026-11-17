using SkillSwap.Application.Auth;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Tests.Auth;

public class TwoFactorServiceTests
{
    private static readonly ClientInfo Client = new("Firefox", "127.0.0.1");

    private readonly AuthFixture _fx = new();

    private User AddUser(bool twoFactorEnabled = false, bool staff = false)
    {
        var user = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "ru");
        user.EmailConfirmed = true;
        user.TwoFactorEnabled = twoFactorEnabled;
        if (staff)
        {
            typeof(User).GetProperty(nameof(User.AccountType))!.SetValue(user, AccountType.Staff);
        }

        _fx.Users.Existing = user;
        return user;
    }

    private string LoginToken(User user) => $"challenge:Login:{user.Id}";
    private string SetupToken(User user) => $"challenge:Setup:{user.Id}";

    // ---------- второй шаг входа ----------

    [Fact]
    public async Task Login_with_a_valid_code_issues_tokens()
    {
        var user = AddUser(twoFactorEnabled: true);

        var result = await _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(LoginToken(user), "123 456", false), Client);

        Assert.Equal($"access-for-{user.Id}", result.Response.AccessToken);   // пробел в коде допускается
        Assert.NotNull(result.RefreshToken);
        Assert.Single(_fx.RefreshTokens.Tokens);
        Assert.Equal(1, _fx.Users.ResetFailedCalls);
    }

    [Fact]
    public async Task Login_with_a_wrong_code_is_401_and_counts_a_failed_attempt()
    {
        var user = AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(LoginToken(user), "000000", false), Client));

        Assert.Equal("invalid_two_factor_code", ex.Code);
        Assert.Equal(1, _fx.Users.AccessFailedCalls);
        Assert.Empty(_fx.RefreshTokens.Tokens);
    }

    [Fact]
    public async Task Login_when_locked_out_is_429_even_with_a_valid_code()
    {
        var user = AddUser(twoFactorEnabled: true);
        _fx.Users.LockedOut = true;

        var ex = await Assert.ThrowsAsync<TooManyRequestsException>(
            () => _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(LoginToken(user), "123456", false), Client));

        Assert.Equal("account_locked_out", ex.Code);
    }

    [Fact]
    public async Task Login_with_a_recovery_code_works_once()
    {
        var user = AddUser(twoFactorEnabled: true);
        _fx.Users.RecoveryCodes.Add("abcde-12345");

        var first = await _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(LoginToken(user), "abcde-12345", true), Client);
        Assert.NotNull(first.RefreshToken);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(LoginToken(user), "abcde-12345", true), Client));
        Assert.Equal("invalid_two_factor_code", ex.Code);
    }

    [Fact]
    public async Task A_recovery_code_keeps_its_dash_and_tolerates_surrounding_spaces()
    {
        var user = AddUser(twoFactorEnabled: true);
        _fx.Users.RecoveryCodes.Add("abcde-12345");

        // Дефис — часть кода (Identity сравнивает как есть), пробелы по краям — лишние.
        var result = await _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(LoginToken(user), "  abcde-12345 ", true), Client);

        Assert.NotNull(result.RefreshToken);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("challenge:Setup:00000000-0000-0000-0000-000000000001")]   // токен настройки не годится для входа
    public async Task Login_with_a_bad_or_wrong_purpose_token_is_401(string token)
    {
        AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(token, "123456", false), Client));

        Assert.Equal("invalid_two_factor_token", ex.Code);
    }

    [Fact]
    public async Task Login_with_a_setup_token_of_a_real_user_is_rejected()
    {
        var user = AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(SetupToken(user), "123456", false), Client));

        Assert.Equal("invalid_two_factor_token", ex.Code);
    }

    [Fact]
    public async Task Login_for_a_blocked_user_is_403()
    {
        var user = AddUser(twoFactorEnabled: true);
        typeof(User).GetProperty(nameof(User.Status))!.SetValue(user, UserStatus.Blocked);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(LoginToken(user), "123456", false), Client));

        Assert.Equal("account_blocked", ex.Code);
    }

    // ---------- настройка ----------

    [Fact]
    public async Task Setup_returns_a_key_and_an_otpauth_uri_but_does_not_enable_2FA()
    {
        var user = AddUser();

        var response = await _fx.TwoFactor.SetupAsync(user.Id, null);

        Assert.Equal("KEY1", response.SharedKey);
        Assert.StartsWith("otpauth://totp/SkillSwap:ali%40example.com?secret=KEY1&issuer=SkillSwap", response.AuthenticatorUri);
        Assert.False(user.TwoFactorEnabled);
    }

    [Fact]
    public async Task Setup_twice_generates_a_new_key()
    {
        var user = AddUser();

        var first = await _fx.TwoFactor.SetupAsync(user.Id, null);
        var second = await _fx.TwoFactor.SetupAsync(user.Id, null);

        Assert.NotEqual(first.SharedKey, second.SharedKey);
    }

    [Fact]
    public async Task Setup_when_already_enabled_is_409()
    {
        var user = AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => _fx.TwoFactor.SetupAsync(user.Id, null));

        Assert.Equal("two_factor_already_enabled", ex.Code);
    }

    [Fact]
    public async Task Setup_for_staff_works_with_a_setup_token()
    {
        var user = AddUser(staff: true);

        var response = await _fx.TwoFactor.SetupAsync(null, new TwoFactorSetupRequest(SetupToken(user)));

        Assert.Equal("KEY1", response.SharedKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    public async Task Setup_without_a_user_or_with_a_bad_token_is_401(string? token)
    {
        AddUser(staff: true);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.TwoFactor.SetupAsync(null, new TwoFactorSetupRequest(token)));

        Assert.Equal("invalid_two_factor_token", ex.Code);
    }

    [Fact]
    public async Task Setup_with_a_login_token_is_rejected()
    {
        var user = AddUser(staff: true);

        await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.TwoFactor.SetupAsync(null, new TwoFactorSetupRequest(LoginToken(user))));
    }

    // ---------- подтверждение ----------

    [Fact]
    public async Task Verify_with_a_valid_code_enables_2FA_and_returns_10_recovery_codes()
    {
        var user = AddUser();
        await _fx.TwoFactor.SetupAsync(user.Id, null);

        var result = await _fx.TwoFactor.VerifyAsync(user.Id, new TwoFactorVerifyRequest("123456", null), Client);

        Assert.True(user.TwoFactorEnabled);
        Assert.Equal(10, result.Response.RecoveryCodes.Count);
        Assert.Equal(10, result.Response.RecoveryCodes.Distinct().Count());
        Assert.Null(result.Response.Auth);        // обычный пользователь уже вошёл, повторный вход не нужен
        Assert.Null(result.RefreshToken);
    }

    [Fact]
    public async Task Verify_with_a_wrong_code_is_400_and_2FA_stays_off()
    {
        var user = AddUser();
        await _fx.TwoFactor.SetupAsync(user.Id, null);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _fx.TwoFactor.VerifyAsync(user.Id, new TwoFactorVerifyRequest("000000", null), Client));

        Assert.Equal("invalid_two_factor_code", ex.Code);
        Assert.False(user.TwoFactorEnabled);
        Assert.Equal(1, _fx.Users.AccessFailedCalls);
    }

    [Fact]
    public async Task Verify_without_setup_is_400()
    {
        var user = AddUser();

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _fx.TwoFactor.VerifyAsync(user.Id, new TwoFactorVerifyRequest("123456", null), Client));

        Assert.Equal("two_factor_not_set_up", ex.Code);
    }

    [Fact]
    public async Task Verify_for_staff_with_a_setup_token_enables_2FA_and_finishes_the_login()
    {
        var user = AddUser(staff: true);
        await _fx.TwoFactor.SetupAsync(null, new TwoFactorSetupRequest(SetupToken(user)));

        var result = await _fx.TwoFactor.VerifyAsync(null, new TwoFactorVerifyRequest("123456", SetupToken(user)), Client);

        Assert.True(user.TwoFactorEnabled);
        Assert.Equal(10, result.Response.RecoveryCodes.Count);
        Assert.Equal($"access-for-{user.Id}", result.Response.Auth!.AccessToken);
        Assert.NotNull(result.RefreshToken);
        Assert.Single(_fx.RefreshTokens.Tokens);
    }

    // ---------- отключение ----------

    [Fact]
    public async Task Disable_with_password_and_code_turns_2FA_off_and_resets_the_key()
    {
        var user = AddUser(twoFactorEnabled: true);
        _fx.Users.AuthenticatorKey = "OLDKEY";

        await _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("abc12345", "123456"));

        Assert.False(user.TwoFactorEnabled);
        Assert.NotEqual("OLDKEY", _fx.Users.AuthenticatorKey);
    }

    [Fact]
    public async Task Disable_with_a_wrong_password_is_400()
    {
        var user = AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("wrong-pass1", "123456")));

        Assert.Equal("invalid_password", ex.Code);
        Assert.True(user.TwoFactorEnabled);
    }

    [Fact]
    public async Task Disable_with_a_wrong_code_is_400()
    {
        var user = AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("abc12345", "000000")));

        Assert.Equal("invalid_two_factor_code", ex.Code);
        Assert.True(user.TwoFactorEnabled);
    }

    [Fact]
    public async Task Disable_is_forbidden_for_staff()
    {
        var user = AddUser(twoFactorEnabled: true, staff: true);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("abc12345", "123456")));

        Assert.Equal("two_factor_mandatory", ex.Code);
        Assert.True(user.TwoFactorEnabled);
    }

    [Fact]
    public async Task Disable_when_2FA_is_off_is_400()
    {
        var user = AddUser();

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("abc12345", "123456")));

        Assert.Equal("two_factor_not_enabled", ex.Code);
    }
}
