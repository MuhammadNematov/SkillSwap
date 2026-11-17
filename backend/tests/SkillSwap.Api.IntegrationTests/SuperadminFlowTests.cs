using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Seeding;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>
/// Суперадмин из переменных окружения (Superadmin__Email / Superadmin__Password): создание при старте,
/// обязательная настройка 2FA при первом входе, невозможность отключить 2FA.
/// Суперадмин один на весь контейнер, поэтому тесты, которые его настраивают, идут одним последовательным сценарием.
/// </summary>
[Collection(ApiCollection.Name)]
public class SuperadminFlowTests
{
    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public SuperadminFlowTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    private Task<User> FindSuperadminAsync() =>
        _factory.QueryAsync(db => db.Set<User>().SingleAsync(u => u.Email == ApiFactory.SuperadminEmail));

    [Fact]
    public async Task The_superadmin_from_the_environment_is_created_at_startup_as_a_staff_account()
    {
        var user = await FindSuperadminAsync();

        Assert.Equal(UserRole.SuperAdmin, user.Role);
        Assert.Equal(AccountType.Staff, user.AccountType);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.True(user.EmailConfirmed);          // письмо подтверждения не нужно
        Assert.Null(user.DateOfBirth);             // анкеты у служебного аккаунта нет
        Assert.False(user.IsSearchable);
        Assert.NotEqual(ApiFactory.SuperadminPassword, user.PasswordHash);   // пароль хранится хешем
        Assert.Empty(_factory.Mail.To(ApiFactory.SuperadminEmail));          // и никаких писем
    }

    [Fact]
    public async Task Seeding_again_does_not_create_a_second_superadmin_or_touch_the_first_one()
    {
        var before = await FindSuperadminAsync();
        var seeder = _factory.Services.GetServices<IHostedService>().OfType<SuperadminSeeder>().Single();

        await seeder.StartAsync(CancellationToken.None);   // повторный старт приложения

        var admins = await _factory.QueryAsync(db => db.Set<User>().CountAsync(u => u.Role == UserRole.SuperAdmin));
        var after = await FindSuperadminAsync();
        Assert.Equal(1, admins);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
    }

    [Fact]
    public async Task Seeder_fails_loudly_when_only_one_of_the_two_variables_is_set()
    {
        var seeder = new SuperadminSeeder(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new SuperadminOptions { Email = "half@skillswap.test", Password = null }),
            NullLogger<SuperadminSeeder>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.StartAsync(CancellationToken.None));

        Assert.Contains("Superadmin__Password", ex.Message);
    }

    [Fact]
    public async Task Seeder_without_any_variables_does_nothing()
    {
        var seeder = new SuperadminSeeder(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new SuperadminOptions()),
            NullLogger<SuperadminSeeder>.Instance);

        await seeder.StartAsync(CancellationToken.None);   // не бросает

        Assert.Equal(1, await _factory.QueryAsync(db => db.Set<User>().CountAsync(u => u.Role == UserRole.SuperAdmin)));
    }

    [Fact]
    public async Task The_superadmin_cannot_log_in_with_a_wrong_password()
    {
        var response = await _api.LoginAsync(ApiFactory.SuperadminEmail, "wrong-pass1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Весь путь: вход без 2FA невозможен → настройка по токену настройки → вход завершается при подтверждении →
    /// дальше вход только в два шага → отключить 2FA нельзя.
    /// </summary>
    [Fact]
    public async Task The_superadmin_must_set_up_2FA_at_first_login_and_can_never_turn_it_off()
    {
        // 1. Первый вход: токенов нет, система требует настроить 2FA.
        var login = await _api.LoginAsync(ApiFactory.SuperadminEmail, ApiFactory.SuperadminPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("requires2faSetup").GetBoolean());
        Assert.False(body.GetProperty("requires2fa").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("accessToken").ValueKind);
        Assert.Null(AuthApi.SetCookieHeader(login));
        var setupToken = body.GetProperty("twoFactorToken").GetString()!;

        // Токен настройки — не access-токен и не годится для второго шага входа.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.MeAsync(setupToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.LoginTwoFactorAsync(setupToken, "123456")).StatusCode);

        // 2. Настройка по токену (Bearer ещё нет) и подтверждение первым кодом.
        var setup = await _api.TwoFactorSetupAsync(accessToken: null, twoFactorToken: setupToken);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;

        var wrongCode = await _api.TwoFactorVerifyAsync(null, _api.TotpCode(key, 5), setupToken);
        Assert.Equal(HttpStatusCode.BadRequest, wrongCode.StatusCode);

        var verify = await _api.TwoFactorVerifyAsync(null, _api.TotpCode(key, -1), setupToken);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var verified = await verify.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(10, verified.GetProperty("recoveryCodes").GetArrayLength());
        var access = verified.GetProperty("auth").GetProperty("accessToken").GetString()!;   // вход завершился сразу
        Assert.NotNull(AuthApi.RefreshTokenFrom(verify));

        var me = await (await _api.MeAsync(access)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ApiFactory.SuperadminEmail, me.GetProperty("email").GetString());
        Assert.Equal("SuperAdmin", me.GetProperty("role").GetString());
        Assert.True((await FindSuperadminAsync()).TwoFactorEnabled);

        // 3. Токен настройки после этого больше не работает (2FA уже включена).
        var again = await _api.TwoFactorSetupAsync(accessToken: null, twoFactorToken: setupToken);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // 4. Следующий вход — в два шага.
        var second = await _api.LoginAsync(ApiFactory.SuperadminEmail, ApiFactory.SuperadminPassword);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(secondBody.GetProperty("requires2fa").GetBoolean());
        Assert.False(secondBody.GetProperty("requires2faSetup").GetBoolean());
        var loginToken = secondBody.GetProperty("twoFactorToken").GetString()!;

        // Токен входа не годится для настройки.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.TwoFactorSetupAsync(null, loginToken)).StatusCode);

        var final = await _api.LoginTwoFactorAsync(loginToken, _api.TotpCode(key));
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
        var finalAccess = await AuthApi.ReadAccessTokenAsync(final);
        Assert.NotNull(AuthApi.RefreshTokenFrom(final));

        // 5. Отключить 2FA нельзя, даже с верными паролем и кодом.
        var disable = await _api.TwoFactorDisableAsync(finalAccess, ApiFactory.SuperadminPassword, _api.TotpCode(key, 1));
        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
        Assert.Equal("two_factor_mandatory", await AuthApi.ReadCodeAsync(disable));
        Assert.True((await FindSuperadminAsync()).TwoFactorEnabled);
    }
}
