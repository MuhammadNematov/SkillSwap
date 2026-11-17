using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>
/// Защита 2FA на настоящей БД: код TOTP работает один раз и только вперёд по времени,
/// резервные коды лежат в базе хешами и тоже одноразовые (в том числе при гонке).
/// </summary>
[Collection(ApiCollection.Name)]
public class TwoFactorHardeningTests
{
    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public TwoFactorHardeningTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    /// <summary>Пользователь с включённой 2FA. Включение уже потратило код прошлого окна (−1).</summary>
    private async Task<(string Email, string Key, IReadOnlyList<string> Codes)> UserWithTwoFactorAsync()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (access, _) = await _api.LoginTokensAsync(email);
        var (key, codes) = await _api.EnableTwoFactorAsync(access);
        return (email, key, codes);
    }

    private async Task<string> NewLoginTokenAsync(string email) =>
        await AuthApi.ReadTwoFactorTokenAsync(await _api.LoginAsync(email));

    private Task<User> FindUserAsync(string email) =>
        _factory.QueryAsync(db => db.Set<User>().SingleAsync(u => u.Email == email));

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    // ---------- повторное использование TOTP-кода ----------

    [Fact]
    public async Task The_same_code_cannot_be_used_twice_but_the_next_one_works()
    {
        var (email, key, _) = await UserWithTwoFactorAsync();

        var first = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var replay = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key));   // тот же код
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("invalid_two_factor_code", await AuthApi.ReadCodeAsync(replay));
        Assert.Null(AuthApi.SetCookieHeader(replay));

        var next = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key, +1));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task An_older_code_is_rejected_after_a_newer_one_was_used()
    {
        var (email, key, _) = await UserWithTwoFactorAsync();

        Assert.Equal(HttpStatusCode.OK,
            (await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key, +1))).StatusCode);

        // Код текущего окна подходит по времени, но он старше уже использованного — отказ.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key, 0))).StatusCode);
    }

    [Fact]
    public async Task The_code_that_enabled_2FA_cannot_be_used_to_log_in()
    {
        var (email, key, _) = await UserWithTwoFactorAsync();   // включали кодом окна −1

        var replay = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key, -1));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        var fresh = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key, 0));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task A_code_used_to_log_in_cannot_be_used_to_disable_2FA()
    {
        var (email, key, _) = await UserWithTwoFactorAsync();
        var login = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key));
        var access = await AuthApi.ReadAccessTokenAsync(login);

        var replay = await _api.TwoFactorDisableAsync(access, AuthApi.Password, _api.TotpCode(key));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_two_factor_code", await AuthApi.ReadCodeAsync(replay));

        var fresh = await _api.TwoFactorDisableAsync(access, AuthApi.Password, _api.TotpCode(key, +1));
        Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);
    }

    [Fact]
    public async Task The_last_used_step_is_stored_in_the_database()
    {
        var (email, key, _) = await UserWithTwoFactorAsync();
        Assert.Equal(_api.CurrentStep() - 1, (await FindUserAsync(email)).TwoFactorLastUsedStep);   // включение

        await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key, +1));

        Assert.Equal(_api.CurrentStep() + 1, (await FindUserAsync(email)).TwoFactorLastUsedStep);
    }

    [Fact]
    public async Task A_failed_code_does_not_move_the_stored_step()
    {
        var (email, key, _) = await UserWithTwoFactorAsync();
        var before = (await FindUserAsync(email)).TwoFactorLastUsedStep;

        await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key, 5));   // не подходит

        Assert.Equal(before, (await FindUserAsync(email)).TwoFactorLastUsedStep);
    }

    [Fact]
    public async Task Two_parallel_requests_with_the_same_code_let_only_one_through()
    {
        var (email, key, _) = await UserWithTwoFactorAsync();
        var token = await NewLoginTokenAsync(email);
        var code = _api.TotpCode(key);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => _api.LoginTwoFactorAsync(token, code)));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task Disabling_2FA_forgets_the_step_so_a_fresh_setup_starts_clean()
    {
        var (email, key, _) = await UserWithTwoFactorAsync();
        var access = await AuthApi.ReadAccessTokenAsync(
            await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key)));

        await _api.TwoFactorDisableAsync(access, AuthApi.Password, _api.TotpCode(key, +1));

        Assert.Null((await FindUserAsync(email)).TwoFactorLastUsedStep);
        // С новым ключом работает и код окна −1, который со старой «памятью» был бы отвергнут.
        var (_, _) = await _api.EnableTwoFactorAsync(access);
    }

    // ---------- резервные коды: хранение ----------

    [Fact]
    public async Task Recovery_codes_are_shown_once_in_a_readable_format_and_stored_only_as_hashes()
    {
        var (email, _, codes) = await UserWithTwoFactorAsync();
        var userId = (await FindUserAsync(email)).Id;

        Assert.Equal(10, codes.Count);
        Assert.All(codes, c => Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){3}$", c));

        var stored = await _factory.QueryAsync(db => db.Set<TwoFactorRecoveryCode>()
            .Where(c => c.UserId == userId).ToListAsync());
        Assert.Equal(10, stored.Count);
        Assert.All(stored, c => Assert.Null(c.UsedAt));
        Assert.All(stored, c => Assert.Matches("^[0-9A-F]{64}$", c.CodeHash));

        // Хеши ровно от показанных кодов, а самих кодов (ни с дефисами, ни без) в таблице нет.
        var hashes = stored.Select(c => c.CodeHash).ToHashSet();
        Assert.Equal(codes.Select(c => Sha256Hex(c.Replace("-", string.Empty))).ToHashSet(), hashes);
        Assert.DoesNotContain(codes, hashes.Contains);
        Assert.DoesNotContain(codes.Select(c => c.Replace("-", string.Empty)), hashes.Contains);
    }

    [Fact]
    public async Task Identity_no_longer_keeps_a_plaintext_copy_of_the_recovery_codes()
    {
        var (email, _, codes) = await UserWithTwoFactorAsync();
        var userId = (await FindUserAsync(email)).Id;

        var tokens = await _factory.QueryAsync(db => db.UserTokens.Where(t => t.UserId == userId).ToListAsync());

        Assert.DoesNotContain(tokens, t => t.Name == "RecoveryCodes");
        Assert.DoesNotContain(tokens, t => codes.Any(c => t.Value?.Contains(c, StringComparison.OrdinalIgnoreCase) == true));
    }

    // ---------- резервные коды: использование ----------

    [Fact]
    public async Task A_recovery_code_works_once_and_is_marked_used_in_the_database()
    {
        var (email, _, codes) = await UserWithTwoFactorAsync();
        var userId = (await FindUserAsync(email)).Id;

        var first = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), codes[0], isRecoveryCode: true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.NotNull(AuthApi.RefreshTokenFrom(first));

        var used = await _factory.QueryAsync(db => db.Set<TwoFactorRecoveryCode>()
            .CountAsync(c => c.UserId == userId && c.UsedAt != null));
        Assert.Equal(1, used);

        var replay = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), codes[0], isRecoveryCode: true);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("invalid_two_factor_code", await AuthApi.ReadCodeAsync(replay));

        // Остальные девять работают.
        var other = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), codes[1], isRecoveryCode: true);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Theory]
    [InlineData("lowercase")]
    [InlineData("no-dashes")]
    [InlineData("spaces")]
    public async Task A_recovery_code_can_be_typed_loosely(string style)
    {
        var (email, _, codes) = await UserWithTwoFactorAsync();
        var typed = style switch
        {
            "lowercase" => codes[0].ToLowerInvariant(),
            "no-dashes" => codes[0].Replace("-", string.Empty),
            _ => " " + codes[0].Replace("-", " ") + " ",
        };

        var response = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), typed, isRecoveryCode: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Two_parallel_requests_with_the_same_recovery_code_let_only_one_through()
    {
        var (email, _, codes) = await UserWithTwoFactorAsync();
        var token = await NewLoginTokenAsync(email);

        var results = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => _api.LoginTwoFactorAsync(token, codes[0], isRecoveryCode: true)));

        var statuses = string.Join(", ", results.Select(r => (int)r.StatusCode));
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.OK) == 1, $"Статусы: {statuses}");
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.Unauthorized) == 1, $"Статусы: {statuses}");
    }

    [Fact]
    public async Task A_recovery_code_of_another_user_does_not_work()
    {
        var (emailA, _, _) = await UserWithTwoFactorAsync();
        var (_, _, codesB) = await UserWithTwoFactorAsync();

        var response = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(emailA), codesB[0], isRecoveryCode: true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_recovery_code_is_not_a_totp_code_and_vice_versa()
    {
        var (email, key, codes) = await UserWithTwoFactorAsync();

        var codeAsTotp = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), codes[0], isRecoveryCode: false);
        var totpAsCode = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key), isRecoveryCode: true);

        Assert.Equal(HttpStatusCode.Unauthorized, codeAsTotp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, totpAsCode.StatusCode);
    }

    [Fact]
    public async Task Disabling_2FA_deletes_all_recovery_codes()
    {
        var (email, key, _) = await UserWithTwoFactorAsync();
        var userId = (await FindUserAsync(email)).Id;
        var access = await AuthApi.ReadAccessTokenAsync(
            await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key)));

        await _api.TwoFactorDisableAsync(access, AuthApi.Password, _api.TotpCode(key, +1));

        Assert.Equal(0, await _factory.QueryAsync(db => db.Set<TwoFactorRecoveryCode>().CountAsync(c => c.UserId == userId)));
    }

    [Fact]
    public async Task Enabling_2FA_again_issues_a_new_set_and_the_old_codes_stop_working()
    {
        var (email, key, oldCodes) = await UserWithTwoFactorAsync();
        var userId = (await FindUserAsync(email)).Id;
        var access = await AuthApi.ReadAccessTokenAsync(
            await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), _api.TotpCode(key)));
        await _api.TwoFactorDisableAsync(access, AuthApi.Password, _api.TotpCode(key, +1));

        var (newKey, newCodes) = await _api.EnableTwoFactorAsync(access);

        Assert.Empty(oldCodes.Intersect(newCodes));
        Assert.Equal(10, await _factory.QueryAsync(db => db.Set<TwoFactorRecoveryCode>().CountAsync(c => c.UserId == userId)));
        var oldCode = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), oldCodes[0], isRecoveryCode: true);
        Assert.Equal(HttpStatusCode.Unauthorized, oldCode.StatusCode);
        var newCode = await _api.LoginTwoFactorAsync(await NewLoginTokenAsync(email), newCodes[0], isRecoveryCode: true);
        Assert.Equal(HttpStatusCode.OK, newCode.StatusCode);
        Assert.NotEqual(key, newKey);
    }
}
