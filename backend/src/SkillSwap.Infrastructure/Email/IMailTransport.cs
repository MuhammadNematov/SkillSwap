namespace SkillSwap.Infrastructure.Email;

/// <summary>
/// Настоящая отправка письма (SMTP или консоль). Её вызывает только фоновая задача: сервисы приложения пользуются
/// <see cref="SkillSwap.Application.Abstractions.IMailSender"/>, который лишь ставит письмо в очередь.
/// </summary>
public interface IMailTransport
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}
