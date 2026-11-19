using Microsoft.Extensions.Logging;

namespace SkillSwap.Infrastructure.Email;

/// <summary>
/// Только для разработки и тестов, пока SMTP не настроен (<c>Smtp:Host</c> пуст): вместо отправки пишет письмо в консоль.
/// В письмах есть токены, поэтому вне разработки приложение без SMTP не запустится (<see cref="SmtpStartupCheck"/>).
/// </summary>
public sealed class ConsoleMailSender : IMailTransport
{
    private readonly ILogger<ConsoleMailSender> _logger;

    public ConsoleMailSender(ILogger<ConsoleMailSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("\n=== ПИСЬМО (консоль) ===\nКому: {To}\nТема: {Subject}\n\n{Body}\n========================",
            to, subject, body);
        return Task.CompletedTask;
    }
}
