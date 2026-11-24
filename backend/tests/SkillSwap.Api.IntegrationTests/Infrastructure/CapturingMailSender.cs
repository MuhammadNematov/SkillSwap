using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SkillSwap.Application.Abstractions;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>Запоминает отправленные письма, чтобы тест мог достать из них код подтверждения или ссылку сброса пароля.</summary>
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

    /// <summary>Достаёт шестизначный код из последнего письма подтверждения, отправленного на этот email.</summary>
    public string LastConfirmationCode(string email)
    {
        var mail = To(email).LastOrDefault(m => CodeRegex().IsMatch(m.Body))
                   ?? throw new InvalidOperationException($"Письма с кодом подтверждения для {email} нет.");
        return CodeRegex().Match(mail.Body).Groups[1].Value;
    }

    /// <summary>Достаёт userId и токен из последней ссылки сброса пароля, отправленной на этот email.</summary>
    public (string UserId, string Token) LastResetLink(string email)
    {
        var mail = To(email).LastOrDefault(m => m.Body.Contains("reset-password?userId="))
                   ?? throw new InvalidOperationException($"Письма со ссылкой сброса для {email} нет.");
        var match = ResetRegex().Match(mail.Body);
        return (match.Groups[1].Value, Uri.UnescapeDataString(match.Groups[2].Value));
    }

    // Код в письме стоит отдельной строкой.
    [GeneratedRegex(@"(?m)^\s*(\d{6})\s*$")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"reset-password\?userId=([0-9a-fA-F-]+)&token=(\S+)")]
    private static partial Regex ResetRegex();
}

public sealed record SentMail(string To, string Subject, string Body);
