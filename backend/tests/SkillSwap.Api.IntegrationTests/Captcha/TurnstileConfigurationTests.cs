using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Options;
using SkillSwap.Infrastructure.Captcha;

namespace SkillSwap.Api.IntegrationTests.Captcha;

/// <summary>Настройки капчи: значения по умолчанию, проверка при старте, защита от тестового секрета в бою.</summary>
public class TurnstileConfigurationTests
{
    private const string DummySecret = "1x0000000000000000000000000000000AA";
    private const string RealLookingSecret = "0x4AAAAAAAxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "SkillSwap";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static Task Start(TurnstileOptions options, string environment) =>
        new TurnstileStartupCheck(Options.Create(options), new FakeEnvironment(environment), NullLogger<TurnstileStartupCheck>.Instance)
            .StartAsync(CancellationToken.None);

    // ---------- значения ----------

    [Fact]
    public void Defaults_are_safe()
    {
        var o = new TurnstileOptions();

        Assert.True(o.Enabled);                  // включена по умолчанию
        Assert.False(o.SendClientIp);            // IP клиента Cloudflare не отдаём без явного согласия
        Assert.Equal(5, o.TimeoutSeconds);
        Assert.Equal("https://challenges.cloudflare.com/turnstile/v0/siteverify", o.VerifyUrl);
        Assert.Null(o.SecretKey);                // секрета в коде нет
    }

    [Fact]
    public void The_secret_is_not_in_the_shipped_configuration_file()
    {
        // Секрет в appsettings.json означал бы, что приложение может молча работать с тестовым ключом.
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        Assert.True(config.GetSection("Turnstile").Exists());
        Assert.Null(config["Turnstile:SecretKey"]);
        Assert.Equal("true", config["Turnstile:Enabled"], ignoreCase: true);
        Assert.Equal("false", config["Turnstile:SendClientIp"], ignoreCase: true);
    }

    [Theory]
    [InlineData("1x0000000000000000000000000000000AA", true)]
    [InlineData("2x0000000000000000000000000000000AA", true)]
    [InlineData("3x0000000000000000000000000000000AA", true)]
    [InlineData(RealLookingSecret, false)]
    [InlineData("0x4AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)]
    [InlineData("1x0000000000000000000000000000000AB", false)]
    [InlineData("4x0000000000000000000000000000000AA", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Cloudflare_test_secrets_are_recognised(string? key, bool expected)
    {
        Assert.Equal(expected, TurnstileOptions.IsDummySecret(key));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(-1)]
    public void A_nonsense_timeout_is_rejected(int seconds)
    {
        var services = new ServiceCollection();
        services.AddOptions<TurnstileOptions>().Configure(o => o.TimeoutSeconds = seconds).ValidateDataAnnotations();

        Assert.Throws<OptionsValidationException>(() =>
            services.BuildServiceProvider().GetRequiredService<IOptions<TurnstileOptions>>().Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    public void A_nonsense_url_is_rejected(string url)
    {
        var services = new ServiceCollection();
        services.AddOptions<TurnstileOptions>().Configure(o => o.VerifyUrl = url).ValidateDataAnnotations();

        Assert.Throws<OptionsValidationException>(() =>
            services.BuildServiceProvider().GetRequiredService<IOptions<TurnstileOptions>>().Value);
    }

    // ---------- проверка при старте ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_enabled_captcha_without_a_secret_stops_the_application(string? secret)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Start(new TurnstileOptions { SecretKey = secret }, Environments.Development));

        Assert.Contains("Turnstile__SecretKey", ex.Message);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("anything-else")]
    public async Task The_test_secret_is_refused_outside_development_and_testing(string environment)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Start(new TurnstileOptions { SecretKey = DummySecret }, environment));

        Assert.Contains("ТЕСТОВЫЙ", ex.Message);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task The_test_secret_is_fine_for_development_and_testing(string environment)
    {
        await Start(new TurnstileOptions { SecretKey = DummySecret }, environment);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Development")]
    public async Task A_real_looking_secret_is_fine_everywhere(string environment)
    {
        await Start(new TurnstileOptions { SecretKey = RealLookingSecret }, environment);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task A_disabled_captcha_needs_no_secret_and_does_not_stop_the_application(string environment)
    {
        await Start(new TurnstileOptions { Enabled = false, SecretKey = null }, environment);
    }

    [Fact]
    public async Task The_error_message_never_contains_the_secret()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Start(new TurnstileOptions { SecretKey = DummySecret }, "Production"));

        Assert.DoesNotContain(DummySecret, ex.Message);
    }
}
