using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.RateLimiting;

namespace SkillSwap.Api.IntegrationTests.RateLimiting;

/// <summary>
/// Выключатель <c>RateLimiting:Enabled=false</c> (в обычном приложении для тестов он выключен): лимитов нет вообще.
/// Нужен для нагрузочного теста (этап 8) и чтобы остальные тесты не упирались в лимиты.
/// </summary>
[Collection(ApiCollection.Name)]
public class RateLimitingDisabledTests
{
    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public RateLimitingDisabledTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory, TestIps.Next());
    }

    [Fact]
    public void The_switch_is_off_in_this_application()
    {
        Assert.False(_factory.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value.Enabled);
    }

    [Fact]
    public async Task No_endpoint_is_limited_when_the_switch_is_off()
    {
        var email = AuthApi.NewEmail();

        // Больше любого из лимитов: 3/час на email, 10/час на IP, 10/мин на вход, 5/час на регистрацию.
        for (var i = 0; i < 15; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await _api.ForgotPasswordAsync(email)).StatusCode);
        }

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _api.LoginAsync(AuthApi.NewEmail(), "wrong-pass1")).StatusCode);
        }

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await _api.RegisterAsync(AuthApi.NewEmail())).StatusCode);
        }
    }

    [Fact]
    public async Task The_authenticated_limit_is_off_too()
    {
        var (access, _) = await _api.LoginTokensAsync(await _api.RegisterConfirmedUserAsync());

        for (var i = 0; i < 130; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await _api.MeAsync(access)).StatusCode);
        }
    }
}
