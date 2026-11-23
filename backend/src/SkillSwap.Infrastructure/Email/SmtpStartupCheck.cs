using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using SkillSwap.Application.Options;

namespace SkillSwap.Infrastructure.Email;

/// <summary>
/// Проверка настроек почты при старте (fail fast). Вне Development и Testing SMTP обязателен: в письмах ссылки с одноразовыми
/// токенами, и «письма в консоль» на боевом сервере означали бы, что пользователи никогда их не получат.
/// </summary>
public sealed class SmtpStartupCheck : IHostedService
{
    private static readonly ParserOptions StrictAddresses = new() { AllowAddressesWithoutDomain = false };

    private readonly SmtpOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SmtpStartupCheck> _logger;

    public SmtpStartupCheck(IOptions<SmtpOptions> options, IHostEnvironment environment, ILogger<SmtpStartupCheck> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Validate(_options, _environment.IsDevelopment() || _environment.IsEnvironment("Testing"), _environment.EnvironmentName);

        if (!_options.CheckCertificateRevocation)
        {
            _logger.LogWarning(
                "Проверка отзыва сертификата SMTP-сервера выключена (Smtp:CheckCertificateRevocation=false). " +
                "Только для локальной разработки; вне Development приложение с этой настройкой не запустится.");
        }

        return Task.CompletedTask;
    }

    public static void Validate(SmtpOptions options, bool isDevelopmentLike, string environmentName)
    {
        if (!options.CheckCertificateRevocation && !isDevelopmentLike)
        {
            throw new InvalidOperationException(
                $"В окружении {environmentName} задано Smtp:CheckCertificateRevocation=false: отозванный (например, украденный) " +
                "сертификат почтового сервера был бы принят. Уберите настройку: эта проверка выключается только в Development.");
        }

        if (!options.IsConfigured)
        {
            if (isDevelopmentLike)
            {
                return;   // письма идут в консоль
            }

            throw new InvalidOperationException(
                $"В окружении {environmentName} не настроена отправка почты (Smtp:Host). Задайте Smtp__Host, Smtp__Port, Smtp__User, " +
                "Smtp__Password и Smtp__FromAddress. Локально письма ловит Mailpit из docker-compose (Smtp:Host=localhost, Smtp:Port=1025, Smtp:Security=None).");
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("Smtp:Port должен быть числом от 1 до 65535.");
        }

        if (options.TimeoutSeconds < 1)
        {
            throw new InvalidOperationException("Smtp:TimeoutSeconds должен быть положительным.");
        }

        if (!MailboxAddress.TryParse(StrictAddresses, options.FromAddress, out _))
        {
            throw new InvalidOperationException("Smtp:FromAddress не является адресом электронной почты.");
        }

        if (!string.IsNullOrEmpty(options.User) && string.IsNullOrEmpty(options.Password))
        {
            throw new InvalidOperationException("Задан Smtp:User, но не задан Smtp:Password.");
        }

        var local = options.Host!.Equals("localhost", StringComparison.OrdinalIgnoreCase) || options.Host == "127.0.0.1" || options.Host == "::1";
        if (!isDevelopmentLike && options.Security == SmtpSecurity.None && !local)
        {
            throw new InvalidOperationException(
                $"В окружении {environmentName} Smtp:Security=None: пароль и письма с токенами ушли бы по сети открытым текстом. Используйте StartTls или SslOnConnect.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
