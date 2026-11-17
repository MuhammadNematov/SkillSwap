using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.RateLimiting;

namespace SkillSwap.Api.IntegrationTests.RateLimiting;

/// <summary>Значения лимитов берутся из конфигурации: приложение с заниженными значениями ведёт себя иначе.</summary>
public class RateLimitingConfigurationTests : IClassFixture<CustomLimitsApiFactory>
{
    private readonly CustomLimitsApiFactory _factory;

    public RateLimitingConfigurationTests(CustomLimitsApiFactory factory)
    {
        _factory = factory;
    }

    private AuthApi NewClient() => new(_factory, TestIps.Next());

    [Fact]
    public void Overridden_values_are_applied_and_the_rest_stay_at_their_defaults()
    {
        var o = _factory.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

        Assert.Equal(2, o.LoginPerIp.PermitLimit);
        Assert.Equal(60, o.LoginPerIp.WindowSeconds);            // окно не переопределяли
        Assert.Equal(1, o.RegisterPerIp.PermitLimit);
        Assert.Equal(1, o.MailPerEmail.PermitLimit);
        Assert.Equal(2, o.MailPerIp.PermitLimit);
        Assert.Equal(5, o.AuthenticatedPerUser.PermitLimit);
        Assert.Equal(1, o.ResetPasswordPerIp.PermitLimit);
        Assert.Equal(2, o.ConfirmEmailPerIp.PermitLimit);
        Assert.Equal(3, o.RefreshPerIp.PermitLimit);
        Assert.Equal(60, o.RefreshPerIp.WindowSeconds);          // окно не переопределяли
        Assert.Equal(3600, o.MailPerEmail.WindowSeconds);
    }

    [Fact]
    public async Task The_login_limit_follows_the_configured_value()
    {
        var api = NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.LoginAsync(AuthApi.NewEmail(), "x1x1x1x1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.LoginAsync(AuthApi.NewEmail(), "x1x1x1x1")).StatusCode);

        await RateLimitAssert.RateLimitedAsync(await api.LoginAsync(AuthApi.NewEmail(), "x1x1x1x1"), 60);   // третий уже лишний
    }

    [Fact]
    public async Task The_register_limit_follows_the_configured_value()
    {
        var api = NewClient();

        Assert.Equal(HttpStatusCode.Created, (await api.RegisterAsync(AuthApi.NewEmail())).StatusCode);

        await RateLimitAssert.RateLimitedAsync(await api.RegisterAsync(AuthApi.NewEmail()), 3600);
    }

    [Fact]
    public async Task The_per_email_limit_follows_the_configured_value()
    {
        var api = NewClient();
        var email = AuthApi.NewEmail();

        Assert.Equal(HttpStatusCode.NoContent, (await api.ForgotPasswordAsync(email)).StatusCode);

        await RateLimitAssert.RateLimitedAsync(await api.ForgotPasswordAsync(email), 3600);   // второй на тот же адрес — отказ
    }

    [Fact]
    public async Task The_per_ip_mail_limit_follows_the_configured_value()
    {
        var api = NewClient();

        Assert.Equal(HttpStatusCode.NoContent, (await api.ForgotPasswordAsync(AuthApi.NewEmail())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.ForgotPasswordAsync(AuthApi.NewEmail())).StatusCode);

        await RateLimitAssert.RateLimitedAsync(await api.ForgotPasswordAsync(AuthApi.NewEmail()), 3600);   // третье письмо с IP
    }

    [Fact]
    public async Task The_reset_confirm_and_refresh_limits_follow_the_configured_values()
    {
        var api = NewClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await api.ResetPasswordAsync(Guid.NewGuid().ToString(), "x", "newpass123")).StatusCode);
        await RateLimitAssert.RateLimitedAsync(await api.ResetPasswordAsync(Guid.NewGuid().ToString(), "x", "newpass123"), 3600);

        await ConsumeAsync(2, () => api.ConfirmEmailAsync(Guid.NewGuid().ToString(), "x"));
        await RateLimitAssert.RateLimitedAsync(await api.ConfirmEmailAsync(Guid.NewGuid().ToString(), "x"), 3600);

        await ConsumeAsync(3, () => api.RefreshAsync("never-issued"));
        await RateLimitAssert.RateLimitedAsync(await api.RefreshAsync("never-issued"), 60);
    }

    private static async Task ConsumeAsync(int count, Func<Task<HttpResponseMessage>> request)
    {
        for (var i = 0; i < count; i++)
        {
            RateLimitAssert.NotRateLimited(await request());
        }
    }

    [Fact]
    public async Task The_authenticated_limit_follows_the_configured_value()
    {
        var api = NewClient();
        // Регистрация с другого IP: у этого лимит на регистрацию — одна в час.
        var email = await NewClient().RegisterConfirmedUserAsync();
        var (access, _) = await api.LoginTokensAsync(email);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await api.MeAsync(access)).StatusCode);
        }

        await RateLimitAssert.RateLimitedAsync(await api.MeAsync(access), 60);
    }
}
