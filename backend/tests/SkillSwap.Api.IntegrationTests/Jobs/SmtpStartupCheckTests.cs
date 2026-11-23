using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Options;
using SkillSwap.Infrastructure.Email;

namespace SkillSwap.Api.IntegrationTests.Jobs;

/// <summary>Почта проверяется при старте: вне разработки без настоящего SMTP приложение не запускается.</summary>
public class SmtpStartupCheckTests
{
    private static SmtpOptions Valid() => new()
    {
        Host = "smtp.example.com", Port = 587, Security = SmtpSecurity.StartTls,
        User = "user", Password = "secret", FromAddress = "no-reply@skillswap.example",
    };

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Without_smtp_development_and_tests_fall_back_to_the_console(string environment) =>
        SmtpStartupCheck.Validate(new SmtpOptions(), isDevelopmentLike: true, environment);

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Without_smtp_other_environments_do_not_start(string environment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SmtpStartupCheck.Validate(new SmtpOptions(), isDevelopmentLike: false, environment));
        Assert.Contains("Smtp:Host", ex.Message);
        Assert.Contains("Mailpit", ex.Message);   // подсказка, как поднять почту локально
    }

    [Fact]
    public void A_proper_production_configuration_is_accepted() =>
        SmtpStartupCheck.Validate(Valid(), isDevelopmentLike: false, "Production");

    [Fact]
    public void Local_mailpit_without_tls_is_fine_for_development()
    {
        var options = new SmtpOptions { Host = "localhost", Port = 1025, Security = SmtpSecurity.None };

        SmtpStartupCheck.Validate(options, isDevelopmentLike: true, "Development");
    }

    [Theory]
    [InlineData("smtp.example.com")]
    [InlineData("10.0.0.5")]
    public void A_remote_server_without_tls_is_refused_in_production(string host)
    {
        var options = Valid();
        options.Host = host;
        options.Security = SmtpSecurity.None;

        var ex = Assert.Throws<InvalidOperationException>(() => SmtpStartupCheck.Validate(options, isDevelopmentLike: false, "Production"));
        Assert.Contains("открытым текстом", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void A_nonsense_port_is_refused(int port)
    {
        var options = Valid();
        options.Port = port;

        Assert.Throws<InvalidOperationException>(() => SmtpStartupCheck.Validate(options, isDevelopmentLike: false, "Production"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("a b@c.d")]
    public void A_bad_sender_address_is_refused(string from)
    {
        var options = Valid();
        options.FromAddress = from;

        Assert.Throws<InvalidOperationException>(() => SmtpStartupCheck.Validate(options, isDevelopmentLike: false, "Production"));
    }

    [Fact]
    public void A_login_without_a_password_is_refused()
    {
        var options = Valid();
        options.Password = null;

        var ex = Assert.Throws<InvalidOperationException>(() => SmtpStartupCheck.Validate(options, isDevelopmentLike: false, "Production"));
        Assert.Contains("Smtp:Password", ex.Message);
    }

    [Fact]
    public void A_non_positive_timeout_is_refused()
    {
        var options = Valid();
        options.TimeoutSeconds = 0;

        Assert.Throws<InvalidOperationException>(() => SmtpStartupCheck.Validate(options, isDevelopmentLike: false, "Production"));
    }

    // Smtp:CheckCertificateRevocation=false нужен только для разработки на macOS (Gmail: в сертификате только CRL).

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Disabled_revocation_check_does_not_start_outside_development(string environment)
    {
        var options = Valid();
        options.CheckCertificateRevocation = false;

        var ex = Assert.Throws<InvalidOperationException>(() => SmtpStartupCheck.Validate(options, isDevelopmentLike: false, environment));
        Assert.Contains("Smtp:CheckCertificateRevocation", ex.Message);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Disabled_revocation_check_starts_in_development_with_a_warning(string environment)
    {
        var options = Valid();
        options.CheckCertificateRevocation = false;
        var logger = new ListLogger();

        await new SmtpStartupCheck(Options.Create(options), new FakeEnvironment(environment), logger).StartAsync(CancellationToken.None);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("Smtp:CheckCertificateRevocation=false", warning.Message);
        Assert.DoesNotContain(options.Password!, warning.Message);   // пароль в журнал не попадает
    }

    [Fact]
    public async Task With_the_revocation_check_on_there_is_no_warning()
    {
        var logger = new ListLogger();

        await new SmtpStartupCheck(Options.Create(Valid()), new FakeEnvironment("Production"), logger).StartAsync(CancellationToken.None);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Defaults_are_safe()
    {
        var o = new SmtpOptions();

        Assert.True(o.CheckCertificateRevocation);          // отзыв сертификата проверяется

        Assert.False(o.IsConfigured);                       // без хоста письма идут только в консоль
        Assert.Equal(SmtpSecurity.StartTls, o.Security);    // по умолчанию с шифрованием
        Assert.Equal(587, o.Port);
        Assert.Null(o.Password);                            // секретов в коде нет
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "SkillSwap";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ListLogger : ILogger<SmtpStartupCheck>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>Настоящее приложение: без SMTP вне разработки оно не запускается.</summary>
[Collection(SkillSwap.Api.IntegrationTests.Infrastructure.ApiCollection.Name)]
public class SmtpStartupIntegrationTests
{
    private readonly SkillSwap.Api.IntegrationTests.Infrastructure.ApiFactory _factory;

    public SmtpStartupIntegrationTests(SkillSwap.Api.IntegrationTests.Infrastructure.ApiFactory factory) => _factory = factory;

    [Fact]
    public void The_application_refuses_to_start_in_production_without_smtp()
    {
        using var app = _factory.WithSettings("Production", ("Smtp:Host", ""));

        var ex = Assert.ThrowsAny<Exception>(() => _ = app.Services);

        Assert.Contains("Smtp:Host", Flatten(ex));
    }

    [Fact]
    public void The_application_refuses_to_start_in_production_with_the_revocation_check_off()
    {
        using var app = _factory.WithSettings("Production",
            ("Smtp:Host", "smtp.example.com"), ("Smtp:Security", "StartTls"), ("Smtp:CheckCertificateRevocation", "false"));

        var ex = Assert.ThrowsAny<Exception>(() => _ = app.Services);

        Assert.Contains("Smtp:CheckCertificateRevocation", Flatten(ex));
    }

    [Fact]
    public void The_same_application_starts_in_testing_without_smtp_and_writes_letters_to_the_console()
    {
        using var app = _factory.WithSettings("Testing", ("Smtp:Host", ""));

        Assert.NotNull(app.Services);   // запустилось
        using var scope = app.Services.CreateScope();
        Assert.IsType<SkillSwap.Infrastructure.Email.ConsoleMailSender>(
            scope.ServiceProvider.GetRequiredService<SkillSwap.Infrastructure.Email.IMailTransport>());
    }

    [Fact]
    public void With_smtp_configured_the_transport_is_smtp()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.IsType<SkillSwap.Infrastructure.Email.SmtpMailTransport>(
            scope.ServiceProvider.GetRequiredService<SkillSwap.Infrastructure.Email.IMailTransport>());
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            parts.Add(e.Message);
        }

        return string.Join(" | ", parts);
    }
}