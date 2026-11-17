using Microsoft.Extensions.Logging;
using SkillSwap.Application.Abstractions;

namespace SkillSwap.Infrastructure.Email;

/// <summary>
/// Только для разработки: вместо отправки пишет письмо в консоль.
/// В письмах есть токены — в production такой отправитель использовать нельзя (будет SMTP).
/// </summary>
public sealed class ConsoleMailSender : IMailSender
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
