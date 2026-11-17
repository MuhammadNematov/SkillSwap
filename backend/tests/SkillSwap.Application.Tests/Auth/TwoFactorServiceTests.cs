using System.Text.RegularExpressions;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Tests.Auth;

public class TwoFactorServiceTests
{
    private static readonly ClientInfo Client = new("Firefox", "127.0.0.1");
    private const string Key = "JBSWY3DPEHPK3PXP";

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

        if (twoFactorEnabled)
        {
            _fx.Users.AuthenticatorKey = Key;   // у включённой 2FA ключ уже есть
        }

        _fx.Users.Existing = user;
        return user;
    }

    private string LoginToken(User user) => $"challenge:Login:{user.Id}";
    private string SetupToken(User user) => $"challenge:Setup:{user.Id}";

    private Task<LoginResult> LoginWith(User user, string code, bool recovery = false) =>
        _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(LoginToken(user), code, recovery), Client);

    // ---------- второй шаг входа ----------

    [Fact]
    public async Task Login_with_a_valid_code_issues_tokens()
    {
        var user = AddUser(twoFactorEnabled: true);
        var code = _fx.TotpCode();

        var result = await LoginWith(user, $"{code[..3]} {code[3..]}");   // пробел в коде допускается

        Assert.Equal($"access-for-{user.Id}", result.Response.AccessToken);
        Assert.NotNull(result.RefreshToken);
        Assert.Single(_fx.RefreshTokens.Tokens);
        Assert.Equal(1, _fx.Users.ResetFailedCalls);
        Assert.True(_fx.Activity.Touched.ContainsKey(user.Id));
    }

    [Theory]
    [InlineData(-1)]   // код из прошлого окна (часы телефона отстают)
    [InlineData(0)]
    [InlineData(1)]    // код из следующего окна (часы телефона спешат)
    public async Task Login_accepts_codes_within_one_step_of_clock_drift(int offset)
    {
        var user = AddUser(twoFactorEnabled: true);

        var result = await LoginWith(user, _fx.TotpCode(offset));

        Assert.NotNull(result.RefreshToken);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    [InlineData(10)]
    public async Task Login_rejects_codes_outside_the_window(int offset)
    {
        var user = AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => LoginWith(user, _fx.TotpCode(offset)));

        Assert.Equal("invalid_two_factor_code", ex.Code);
    }

    [Fact]
    public async Task Login_with_a_wrong_code_is_401_and_counts_a_failed_attempt()
    {
        var user = AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => LoginWith(user, _fx.TotpCode(5)));

        Assert.Equal("invalid_two_factor_code", ex.Code);
        Assert.Equal(1, _fx.Users.AccessFailedCalls);
        Assert.Empty(_fx.RefreshTokens.Tokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public async Task Login_with_a_malformed_code_is_401(string code)
    {
        var user = AddUser(twoFactorEnabled: true);

        // Пустую строку отсекает валидатор запроса, остальное — проверка кода.
        if (code.Length == 0)
        {
            await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => LoginWith(user, code));
        }
        else
        {
            var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => LoginWith(user, code));
            Assert.Equal("invalid_two_factor_code", ex.Code);
        }
    }

    [Fact]
    public async Task Login_when_locked_out_is_429_even_with_a_valid_code()
    {
        var user = AddUser(twoFactorEnabled: true);
        _fx.Users.LockedOut = true;

        var ex = await Assert.ThrowsAsync<TooManyRequestsException>(() => LoginWith(user, _fx.TotpCode()));

        Assert.Equal("account_locked_out", ex.Code);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("challenge:Setup:00000000-0000-0000-0000-000000000001")]   // токен настройки не годится для входа
    public async Task Login_with_a_bad_or_wrong_purpose_token_is_401(string token)
    {
        AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(token, _fx.TotpCode(), false), Client));

        Assert.Equal("invalid_two_factor_token", ex.Code);
    }

    [Fact]
    public async Task Login_with_a_setup_token_of_a_real_user_is_rejected()
    {
        var user = AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(
            () => _fx.TwoFactor.LoginAsync(new LoginTwoFactorRequest(SetupToken(user), _fx.TotpCode(), false), Client));

        Assert.Equal("invalid_two_factor_token", ex.Code);
    }

    [Fact]
    public async Task Login_for_a_blocked_user_is_403()
    {
        var user = AddUser(twoFactorEnabled: true);
        typeof(User).GetProperty(nameof(User.Status))!.SetValue(user, UserStatus.Blocked);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => LoginWith(user, _fx.TotpCode()));

        Assert.Equal("account_blocked", ex.Code);
    }

    // ---------- повторное использование TOTP-кода ----------

    [Fact]
    public async Task The_same_code_cannot_be_used_twice()
    {
        var user = AddUser(twoFactorEnabled: true);
        var code = _fx.TotpCode();
        await LoginWith(user, code);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => LoginWith(user, code));

        Assert.Equal("invalid_two_factor_code", ex.Code);
        Assert.Equal(1, _fx.Users.AccessFailedCalls);   // повтор — это попытка подбора, она считается
    }

    [Fact]
    public async Task An_older_code_is_rejected_after_a_newer_one_was_used()
    {
        var user = AddUser(twoFactorEnabled: true);
        await LoginWith(user, _fx.TotpCode(+1));   // использовали код следующего окна

        // Код текущего и прошлого окна теперь старше последнего использованного: нельзя.
        await Assert.ThrowsAsync<UnauthorizedException>(() => LoginWith(user, _fx.TotpCode(0)));
        await Assert.ThrowsAsync<UnauthorizedException>(() => LoginWith(user, _fx.TotpCode(-1)));
    }

    [Fact]
    public async Task Codes_in_ascending_order_each_work_once()
    {
        var user = AddUser(twoFactorEnabled: true);

        await LoginWith(user, _fx.TotpCode(-1));
        await LoginWith(user, _fx.TotpCode(0));
        await LoginWith(user, _fx.TotpCode(+1));

        Assert.Equal(Totp.StepAt(_fx.Clock.GetUtcNow()) + 1, _fx.Steps.LastStep[user.Id]);
    }

    [Fact]
    public async Task A_code_used_to_enable_2FA_cannot_be_used_to_log_in()
    {
        var user = AddUser();
        await _fx.TwoFactor.SetupAsync(user.Id, null);
        var code = _fx.TotpCode();
        await _fx.TwoFactor.VerifyAsync(user.Id, new TwoFactorVerifyRequest(code, null), Client);

        await Assert.ThrowsAsync<UnauthorizedException>(() => LoginWith(user, code));

        await LoginWith(user, _fx.TotpCode(+1));   // следующий код — пожалуйста
    }

    [Fact]
    public async Task A_code_used_to_log_in_cannot_be_used_to_disable_2FA()
    {
        var user = AddUser(twoFactorEnabled: true);
        var code = _fx.TotpCode();
        await LoginWith(user, code);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("abc12345", code)));

        Assert.Equal("invalid_two_factor_code", ex.Code);
        Assert.True(user.TwoFactorEnabled);
    }

    [Fact]
    public async Task Setting_up_a_new_key_forgets_the_used_steps()
    {
        var user = AddUser();
        await _fx.TwoFactor.SetupAsync(user.Id, null);
        await _fx.TwoFactor.VerifyAsync(user.Id, new TwoFactorVerifyRequest(_fx.TotpCode(), null), Client);
        Assert.True(_fx.Steps.LastStep.ContainsKey(user.Id));

        await _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("abc12345", _fx.TotpCode(+1)));

        Assert.False(_fx.Steps.LastStep.ContainsKey(user.Id));   // ключ сменился, старая «память» не нужна
    }

    // ---------- резервные коды ----------

    [Fact]
    public async Task Recovery_codes_are_stored_only_as_hashes()
    {
        var user = AddUser();
        await _fx.TwoFactor.SetupAsync(user.Id, null);

        var result = await _fx.TwoFactor.VerifyAsync(user.Id, new TwoFactorVerifyRequest(_fx.TotpCode(), null), Client);

        var shown = result.Response.RecoveryCodes;
        Assert.Equal(10, shown.Count);
        Assert.Equal(10, shown.Distinct().Count());
        Assert.All(shown, c => Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){3}$", c));

        var stored = _fx.RecoveryCodes.Codes.Select(c => c.Hash).ToList();
        Assert.Equal(10, stored.Count);
        Assert.Empty(stored.Intersect(shown));                       // открытых кодов в хранилище нет
        Assert.Empty(stored.Intersect(shown.Select(c => c.Replace("-", string.Empty))));
        Assert.All(stored, h => Assert.Matches("^[0-9A-F]{64}$", h));
        Assert.Equal(shown.Select(RecoveryCodes.Hash).OrderBy(h => h), stored.OrderBy(h => h));
    }

    [Fact]
    public async Task A_recovery_code_logs_in_once()
    {
        var user = AddUser(twoFactorEnabled: true);
        var code = await EnableAndGetCodes(user);

        var first = await LoginWith(user, code, recovery: true);
        Assert.NotNull(first.RefreshToken);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => LoginWith(user, code, recovery: true));
        Assert.Equal("invalid_two_factor_code", ex.Code);
        Assert.Equal(1, _fx.Users.AccessFailedCalls);
    }

    [Theory]
    [InlineData("as-is")]
    [InlineData("lower")]
    [InlineData("no-dashes")]
    [InlineData("spaces")]
    public async Task A_recovery_code_may_be_typed_in_any_reasonable_way(string style)
    {
        var user = AddUser(twoFactorEnabled: true);
        var code = await EnableAndGetCodes(user);
        var typed = style switch
        {
            "lower" => code.ToLowerInvariant(),
            "no-dashes" => code.Replace("-", string.Empty),
            "spaces" => "  " + code.Replace("-", " ") + " ",
            _ => code,
        };

        var result = await LoginWith(user, typed, recovery: true);

        Assert.NotNull(result.RefreshToken);
    }

    [Fact]
    public async Task A_wrong_recovery_code_is_401_and_counts_a_failed_attempt()
    {
        var user = AddUser(twoFactorEnabled: true);
        await EnableAndGetCodes(user);

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => LoginWith(user, "AAAA-BBBB-CCCC-DDDD", recovery: true));

        Assert.Equal("invalid_two_factor_code", ex.Code);
        Assert.Equal(1, _fx.Users.AccessFailedCalls);
    }

    [Fact]
    public async Task A_recovery_code_of_another_user_does_not_work()
    {
        var alice = AddUser(twoFactorEnabled: true);
        var aliceCode = await EnableAndGetCodes(alice);
        var bob = User.CreateRegular("bob@example.com", "Bob", new DateOnly(2000, 1, 1), "en");
        bob.EmailConfirmed = true;
        bob.TwoFactorEnabled = true;
        _fx.Users.Existing = bob;

        await Assert.ThrowsAsync<UnauthorizedException>(() => LoginWith(bob, aliceCode, recovery: true));
    }

    [Fact]
    public async Task Enabling_again_after_disable_replaces_all_recovery_codes()
    {
        var user = AddUser();
        await _fx.TwoFactor.SetupAsync(user.Id, null);
        var firstSet = (await _fx.TwoFactor.VerifyAsync(user.Id, new TwoFactorVerifyRequest(_fx.TotpCode(), null), Client)).Response.RecoveryCodes;
        await _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("abc12345", _fx.TotpCode(+1)));
        Assert.Empty(_fx.RecoveryCodes.Codes);   // при отключении коды удалены

        await _fx.TwoFactor.SetupAsync(user.Id, null);
        _fx.Clock.Advance(TimeSpan.FromMinutes(5));   // шаги сброшены, но пусть будет новое окно
        var secondSet = (await _fx.TwoFactor.VerifyAsync(user.Id, new TwoFactorVerifyRequest(_fx.TotpCode(), null), Client)).Response.RecoveryCodes;

        Assert.Empty(firstSet.Intersect(secondSet));
        Assert.Equal(10, _fx.RecoveryCodes.Codes.Count);
        await Assert.ThrowsAsync<UnauthorizedException>(() => LoginWith(user, firstSet[0], recovery: true));
    }

    private async Task<string> EnableAndGetCodes(User user)
    {
        // Включённая 2FA: записываем набор кодов так же, как это делает подтверждение.
        var codes = RecoveryCodes.Generate(TwoFactorService.RecoveryCodeCount);
        await _fx.RecoveryCodes.ReplaceAsync(user.Id, codes.Select(RecoveryCodes.Hash).ToList());
        user.TwoFactorEnabled = true;
        _fx.Users.AuthenticatorKey ??= Key;
        return codes[0];
    }

    // ---------- настройка ----------

    [Fact]
    public async Task Setup_returns_a_key_and_an_otpauth_uri_but_does_not_enable_2FA()
    {
        var user = AddUser();

        var response = await _fx.TwoFactor.SetupAsync(user.Id, null);

        Assert.Equal(_fx.Users.AuthenticatorKey, response.SharedKey);
        Assert.StartsWith(
            $"otpauth://totp/SkillSwap:ali%40example.com?secret={response.SharedKey}&issuer=SkillSwap",
            response.AuthenticatorUri);
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

        Assert.Equal(_fx.Users.AuthenticatorKey, response.SharedKey);
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

        var result = await _fx.TwoFactor.VerifyAsync(user.Id, new TwoFactorVerifyRequest(_fx.TotpCode(), null), Client);

        Assert.True(user.TwoFactorEnabled);
        Assert.Equal(10, result.Response.RecoveryCodes.Count);
        Assert.Null(result.Response.Auth);        // обычный пользователь уже вошёл, повторный вход не нужен
        Assert.Null(result.RefreshToken);
    }

    [Fact]
    public async Task Verify_with_a_wrong_code_is_400_and_2FA_stays_off()
    {
        var user = AddUser();
        await _fx.TwoFactor.SetupAsync(user.Id, null);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _fx.TwoFactor.VerifyAsync(user.Id, new TwoFactorVerifyRequest(_fx.TotpCode(5), null), Client));

        Assert.Equal("invalid_two_factor_code", ex.Code);
        Assert.False(user.TwoFactorEnabled);
        Assert.Empty(_fx.RecoveryCodes.Codes);
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

        var result = await _fx.TwoFactor.VerifyAsync(null, new TwoFactorVerifyRequest(_fx.TotpCode(), SetupToken(user)), Client);

        Assert.True(user.TwoFactorEnabled);
        Assert.Equal(10, result.Response.RecoveryCodes.Count);
        Assert.Equal($"access-for-{user.Id}", result.Response.Auth!.AccessToken);
        Assert.NotNull(result.RefreshToken);
        Assert.Single(_fx.RefreshTokens.Tokens);
    }

    // ---------- отключение ----------

    [Fact]
    public async Task Disable_with_password_and_code_turns_2FA_off_resets_the_key_and_deletes_recovery_codes()
    {
        var user = AddUser(twoFactorEnabled: true);
        await EnableAndGetCodes(user);

        await _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("abc12345", _fx.TotpCode()));

        Assert.False(user.TwoFactorEnabled);
        Assert.NotEqual(Key, _fx.Users.AuthenticatorKey);
        Assert.Empty(_fx.RecoveryCodes.Codes);
    }

    [Fact]
    public async Task Disable_with_a_wrong_password_is_400()
    {
        var user = AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("wrong-pass1", _fx.TotpCode())));

        Assert.Equal("invalid_password", ex.Code);
        Assert.True(user.TwoFactorEnabled);
    }

    [Fact]
    public async Task Disable_with_a_wrong_code_is_400()
    {
        var user = AddUser(twoFactorEnabled: true);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("abc12345", _fx.TotpCode(5))));

        Assert.Equal("invalid_two_factor_code", ex.Code);
        Assert.True(user.TwoFactorEnabled);
    }

    [Fact]
    public async Task Disable_is_forbidden_for_staff()
    {
        var user = AddUser(twoFactorEnabled: true, staff: true);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => _fx.TwoFactor.DisableAsync(user.Id, new TwoFactorDisableRequest("abc12345", _fx.TotpCode())));

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
