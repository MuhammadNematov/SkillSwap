using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SkillSwap.Application.Abstractions;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>Запоминает отправленные письма, чтобы тест мог достать из них ссылку подтверждения.</summary>
public sealed partial class CapturingMailSender : IMailSender
{
    private readonly ConcurrentQueue<SentMail> _sent = new();

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(new SentMail(to, subject, body));
        return Task.CompletedTask;
    }

    public IReadOnlyList<SentMail> To(string email) =>
        _sent.Where(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Достаёт userId и токен из последней ссылки подтверждения, отправленной на этот email.</summary>
    public (string UserId, string Token) LastConfirmationLink(string email)
    {
        var mail = To(email).LastOrDefault() ?? throw new InvalidOperationException($"Письма для {email} нет.");
        var match = LinkRegex().Match(mail.Body);
        if (!match.Success)
        {
            throw new InvalidOperationException($"В письме нет ссылки подтверждения: {mail.Body}");
        }

        return (match.Groups[1].Value, Uri.UnescapeDataString(match.Groups[2].Value));
    }

    [GeneratedRegex(@"confirm-email\?userId=([0-9a-fA-F-]+)&token=(\S+)")]
    private static partial Regex LinkRegex();
}

public sealed record SentMail(string To, string Subject, string Body);
