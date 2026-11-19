using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using SkillSwap.Application.Abstractions;

namespace SkillSwap.Infrastructure.Email;

/// <summary>
/// То, чем приложение отправляет письма: ставит письмо в очередь и сразу возвращается. Саму отправку по SMTP делает фоновая
/// задача (<see cref="MailJobs"/>) с повторами при сбое. Так запрос не ждёт почтовый сервер, а ответ «забыл пароль» не выдаёт по времени,
/// есть ли такой email.
/// Тело письма (в нём токены ссылок) кладётся в очередь зашифрованным: задачи лежат в БД и видны в панели Hangfire.
/// </summary>
public sealed class BackgroundMailSender : IMailSender
{
    public const string ProtectorPurpose = "SkillSwap.MailJobs.v1";

    private readonly IBackgroundJobs _jobs;
    private readonly IDataProtector _protector;

    public BackgroundMailSender(IBackgroundJobs jobs, IDataProtectionProvider dataProtection)
    {
        _jobs = jobs;
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
    }

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        var protectedBody = _protector.Protect(body);
        _jobs.Enqueue<MailJobs>(j => j.SendAsync(to, subject, protectedBody));
        return Task.CompletedTask;
    }
}

/// <summary>Фоновая задача отправки одного письма.</summary>
public sealed class MailJobs
{
    private readonly IMailTransport _transport;
    private readonly IDataProtector _protector;

    public MailJobs(IMailTransport transport, IDataProtectionProvider dataProtection)
    {
        _transport = transport;
        _protector = dataProtection.CreateProtector(BackgroundMailSender.ProtectorPurpose);
    }

    /// <summary>Сбой SMTP повторяется до 5 раз с растущей паузой; после этого задача остаётся в списке неудачных.</summary>
    [AutomaticRetry(Attempts = 5, DelaysInSeconds = [10, 30, 120, 600, 3600])]
    public Task SendAsync(string to, string subject, string protectedBody) =>
        _transport.SendAsync(to, subject, _protector.Unprotect(protectedBody));
}
