using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SkillSwap.Api.IntegrationTests.Infrastructure;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>2FA TOTP на настоящем Identity и PostgreSQL: настройка, вход в два шага, резервные коды, блокировка, отключение.</summary>
[Collection(ApiCollection.Name)]
public class TwoFactorFlowTests
{
    private readonly AuthApi _api;

    public TwoFactorFlowTests(ApiFactory factory)
    {
        _api = new AuthApi(factory);
    }

    private async Task<(string Email, string Access, string Key, IReadOnlyList<string> RecoveryCodes)> UserWithTwoFactorAsync()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (access, _) = await _api.LoginTokensAsync(email);
        var (key, codes) = await _api.EnableTwoFactorAsync(access);
        return (email, access, key, codes);
    }

    // ---------- настройка ----------

    [Fact]
    public async Task Setup_returns_a_key_and_a_QR_uri_and_does_not_enable_2FA()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (access, _) = await _api.LoginTokensAsync(email);

        var response = await _api.TwoFactorSetupAsync(access);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var key = json.GetProperty("sharedKey").GetString()!;
        Assert.Matches("^[A-Z2-7]{16,}$", key);
        var uri = json.GetProperty("authenticatorUri").GetString()!;
        Assert.StartsWith("otpauth://totp/SkillSwap:", uri);
        Assert.Contains($"secret={key}", uri);

        // 2FA ещё не включена: вход по-прежнему в один шаг.
        var login = await _api.LoginAsync(email);
        Assert.False(await AuthApi.ReadFlagAsync(login, "requires2fa"));
        Assert.NotNull(AuthApi.RefreshTokenFrom(login));
    }

    [Fact]
    public async Task Setup_without_a_token_is_401()
    {
        var response = await _api.TwoFactorSetupAsync(accessToken: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Verify_with_a_wrong_code_gives_400_and_2FA_stays_off()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (access, _) = await _api.LoginTokensAsync(email);
        await _api.TwoFactorSetupAsync(access);

        var response = await _api.TwoFactorVerifyAsync(access, "000000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_two_factor_code", await AuthApi.ReadCodeAsync(response));
        Assert.False(await AuthApi.ReadFlagAsync(await _api.LoginAsync(email), "requires2fa"));
    }

    [Fact]
    public async Task Verify_enables_2FA_and_returns_10_unique_recovery_codes()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (access, _) = await _api.LoginTokensAsync(email);

        var (_, codes) = await _api.EnableTwoFactorAsync(access);

        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());
        Assert.True(await AuthApi.ReadFlagAsync(await _api.LoginAsync(email), "requires2fa"));
    }

    [Fact]
    public async Task Setup_when_2FA_is_already_enabled_gives_409()
    {
        var (_, access, _, _) = await UserWithTwoFactorAsync();

        var response = await _api.TwoFactorSetupAsync(access);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("two_factor_already_enabled", await AuthApi.ReadCodeAsync(response));
    }

    // ---------- вход в два шага ----------

    [Fact]
    public async Task Login_with_2FA_needs_a_code_and_issues_neither_tokens_nor_cookie_at_the_first_step()
    {
        var (email, _, _, _) = await UserWithTwoFactorAsync();

        var login = await _api.LoginAsync(email);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var json = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("requires2fa").GetBoolean());
        Assert.False(json.GetProperty("requires2faSetup").GetBoolean());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("twoFactorToken").GetString()));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("accessToken").ValueKind);
        Assert.Null(AuthApi.SetCookieHeader(login));
    }

    [Fact]
    public async Task Second_step_with_a_valid_TOTP_code_gives_tokens_and_the_refresh_cookie()
    {
        var (email, _, key, _) = await UserWithTwoFactorAsync();
        var token = await AuthApi.ReadTwoFactorTokenAsync(await _api.LoginAsync(email));

        var response = await _api.LoginTwoFactorAsync(token, _api.TotpCode(key));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var access = await AuthApi.ReadAccessTokenAsync(response);
        Assert.NotNull(AuthApi.RefreshTokenFrom(response));
        Assert.Equal(HttpStatusCode.OK, (await _api.MeAsync(access)).StatusCode);
    }

    [Fact]
    public async Task Second_step_accepts_the_code_with_a_space_in_the_middle()
    {
        var (email, _, key, _) = await UserWithTwoFactorAsync();
        var token = await AuthApi.ReadTwoFactorTokenAsync(await _api.LoginAsync(email));
        var code = _api.TotpCode(key);

        var response = await _api.LoginTwoFactorAsync(token, $"{code[..3]} {code[3..]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Second_step_with_a_wrong_code_gives_401()
    {
        var (email, _, key, _) = await UserWithTwoFactorAsync();
        var token = await AuthApi.ReadTwoFactorTokenAsync(await _api.LoginAsync(email));
        var wrong = _api.TotpCode(key, 5);   // код из другого окна — гарантированно не подходит

        var response = await _api.LoginTwoFactorAsync(token, wrong);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_two_factor_code", await AuthApi.ReadCodeAsync(response));
        Assert.Null(AuthApi.SetCookieHeader(response));
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_account_so_the_code_cannot_be_brute_forced()
    {
        var (email, _, key, _) = await UserWithTwoFactorAsync();
        var token = await AuthApi.ReadTwoFactorTokenAsync(await _api.LoginAsync(email));
        var wrong = _api.TotpCode(key, 5);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _api.LoginTwoFactorAsync(token, wrong)).StatusCode);
        }

        // Теперь не пускает даже с верным кодом, и пароль тоже не поможет: аккаунт заблокирован на 15 минут.
        var withRightCode = await _api.LoginTwoFactorAsync(token, _api.TotpCode(key));
        Assert.Equal(HttpStatusCode.TooManyRequests, withRightCode.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _api.LoginAsync(email)).StatusCode);
    }

    [Fact]
    public async Task Garbage_or_missing_second_step_token_gives_401()
    {
        var (_, _, key, _) = await UserWithTwoFactorAsync();

        var response = await _api.LoginTwoFactorAsync("garbage", _api.TotpCode(key));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_two_factor_token", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task The_two_factor_token_is_not_an_access_token()
    {
        var (email, _, _, _) = await UserWithTwoFactorAsync();
        var token = await AuthApi.ReadTwoFactorTokenAsync(await _api.LoginAsync(email));

        var response = await _api.MeAsync(token);   // промежуточный токен нельзя подставить как access

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_of_one_user_cannot_be_used_with_a_code_of_another()
    {
        var (emailA, _, _, _) = await UserWithTwoFactorAsync();
        var (_, _, keyB, _) = await UserWithTwoFactorAsync();
        var tokenA = await AuthApi.ReadTwoFactorTokenAsync(await _api.LoginAsync(emailA));

        var response = await _api.LoginTwoFactorAsync(tokenA, _api.TotpCode(keyB));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------- резервные коды ----------

    [Fact]
    public async Task A_recovery_code_logs_in_once_and_then_stops_working()
    {
        var (email, _, _, codes) = await UserWithTwoFactorAsync();
        var token = await AuthApi.ReadTwoFactorTokenAsync(await _api.LoginAsync(email));

        var first = await _api.LoginTwoFactorAsync(token, codes[0], isRecoveryCode: true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(AuthApi.RefreshTokenFrom(first));

        var second = await _api.LoginTwoFactorAsync(token, codes[0], isRecoveryCode: true);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);

        // Другие резервные коды остаются рабочими.
        var other = await _api.LoginTwoFactorAsync(token, codes[1], isRecoveryCode: true);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    // ---------- отключение ----------

    [Fact]
    public async Task Disable_with_password_and_code_returns_login_to_one_step()
    {
        var (email, access, key, _) = await UserWithTwoFactorAsync();

        var response = await _api.TwoFactorDisableAsync(access, AuthApi.Password, _api.TotpCode(key));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var login = await _api.LoginAsync(email);
        Assert.False(await AuthApi.ReadFlagAsync(login, "requires2fa"));
        Assert.NotNull(AuthApi.RefreshTokenFrom(login));
    }

    [Fact]
    public async Task Disable_with_a_wrong_password_gives_400_and_2FA_stays_on()
    {
        var (email, access, key, _) = await UserWithTwoFactorAsync();

        var response = await _api.TwoFactorDisableAsync(access, "wrong-pass1", _api.TotpCode(key));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_password", await AuthApi.ReadCodeAsync(response));
        Assert.True(await AuthApi.ReadFlagAsync(await _api.LoginAsync(email), "requires2fa"));
    }

    [Fact]
    public async Task Disable_without_a_token_is_401()
    {
        var response = await _api.TwoFactorDisableAsync(accessToken: "garbage", AuthApi.Password, "123456");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
