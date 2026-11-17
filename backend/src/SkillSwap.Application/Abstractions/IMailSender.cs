namespace SkillSwap.Application.Abstractions;

/// <summary>Отправка писем. В разработке — вывод в консоль, позже — SMTP (Infrastructure).</summary>
public interface IMailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default);
}
