using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using SkillSwap.Application.Options;

namespace SkillSwap.Infrastructure.Email;

/// <summary>Отправка письма по SMTP (MailKit). Тело в UTF-8, письмо простым текстом.</summary>
public sealed class SmtpMailTransport : IMailTransport
{
    private static readonly ParserOptions StrictAddresses = new() { AllowAddressesWithoutDomain = false };

    private readonly SmtpOptions _options;

    public SmtpMailTransport(IOptions<SmtpOptions> options)
    {
        _options = options.Value;
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        // Строгий разбор: адрес без домена («user») отвергается, а не уходит на сервер.
        message.To.Add(MailboxAddress.Parse(StrictAddresses, to));

        // MimeKit кодирует заголовок целиком: перевод строки в теме не превращается в новый заголовок (защита от инъекции заголовков).
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient
        {
            Timeout = _options.TimeoutSeconds * 1000,
            CheckCertificateRevocation = _options.CheckCertificateRevocation,
        };
        await client.ConnectAsync(_options.Host!, _options.Port, Map(_options.Security), cancellationToken);

        if (!string.IsNullOrEmpty(_options.User))
        {
            await client.AuthenticateAsync(_options.User, _options.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }

    private static SecureSocketOptions Map(SmtpSecurity security) => security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.StartTls,
    };
}
