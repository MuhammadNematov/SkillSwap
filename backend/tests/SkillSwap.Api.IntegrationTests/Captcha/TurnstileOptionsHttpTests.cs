using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Options;

namespace SkillSwap.Api.IntegrationTests.Captcha;

/// <summary>Настройка Turnstile:SendClientIp: IP клиента уходит в Cloudflare только если это разрешено.</summary>
[Collection(OwnHostsCollection.Name)]
public class TurnstileSendIpTests : IClassFixture<CaptchaSendIpApiFactory>
{
    private readonly CaptchaSendIpApiFactory _factory;

    public TurnstileSendIpTests(CaptchaSendIpApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void The_switch_is_on_in_this_application()
    {
        Assert.True(_factory.Services.GetRequiredService<IOptions<TurnstileOptions>>().Value.SendClientIp);
    }

    [Theory]
    [InlineData("198.51.100.23")]
    [InlineData("2001:db8::5")]
    public async Task The_client_ip_is_passed_to_cloudflare_as_remoteip(string ip)
    {
        var api = new AuthApi(_factory, ip) { DefaultCaptcha = FakeCloudflareHandler.PassToken };
        var before = _factory.Cloudflare.Calls.Count;

        var response = await api.RegisterAsync(AuthApi.NewEmail());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(ip, Assert.Single(_factory.Cloudflare.Calls.Skip(before)).Form["remoteip"]);
    }

    [Fact]
    public async Task A_request_rejected_before_the_check_sends_nothing()
    {
        var api = new AuthApi(_factory, "198.51.100.24");   // без токена
        var before = _factory.Cloudflare.Calls.Count;

        var response = await api.PostAsync("/auth/login", new { email = AuthApi.NewEmail(), password = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, _factory.Cloudflare.Calls.Count);   // токена нет — IP никуда не отправляется
    }
}

/// <summary>
/// Выключенная капча (в обычном приложении для тестов она выключена): токен не нужен, Cloudflare не вызывается.
/// Нужна для локальной отладки и нагрузочного теста; на боевом сервере капча должна быть включена.
/// </summary>
[Collection(ApiCollection.Name)]
public class TurnstileDisabledTests
{
    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public TurnstileDisabledTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    [Fact]
    public void The_switch_is_off_in_this_application()
    {
        Assert.False(_factory.Services.GetRequiredService<IOptions<TurnstileOptions>>().Value.Enabled);
    }

    [Fact]
    public async Task Register_login_and_forgot_password_work_without_a_token_and_cloudflare_is_never_called()
    {
        var before = _factory.Cloudflare.Calls.Count;
        var email = AuthApi.NewEmail();

        Assert.Equal(HttpStatusCode.Created, (await _api.RegisterAsync(email)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.LoginAsync(email)).StatusCode);          // дошли до обработчика: email не подтверждён
        Assert.Equal(HttpStatusCode.NoContent, (await _api.ForgotPasswordAsync(email)).StatusCode);

        Assert.Equal(before, _factory.Cloudflare.Calls.Count);
    }

    [Fact]
    public async Task A_token_that_is_sent_anyway_is_ignored_and_not_verified()
    {
        var before = _factory.Cloudflare.Calls.Count;

        var response = await _api.RegisterAsync(AuthApi.NewEmail(), captchaToken: "bot-token");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(before, _factory.Cloudflare.Calls.Count);
    }
}
