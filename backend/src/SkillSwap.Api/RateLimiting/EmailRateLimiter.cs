using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace SkillSwap.Api.RateLimiting;

/// <summary>
/// Лимит писем на один адрес: 3 в час (общий для forgot-password и resend-confirmation).
/// Считается по тексту email, а не по тому, зарегистрирован ли он, поэтому по лимиту нельзя узнать,
/// кто зарегистрирован: для известного и неизвестного адреса он срабатывает на одном и том же запросе.
/// </summary>
public sealed class EmailRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public EmailRateLimiter(IOptions<RateLimitingOptions> options, TimeProvider clock)
    {
        var rule = options.Value.MailPerEmail;
        _limiter = PartitionedRateLimiter.Create<string, string>(email => RateLimitPartition.Get(
            email,
            _ => new SlidingLogRateLimiter(rule.PermitLimit, rule.Window, clock)));
    }

    /// <summary>Пробует занять одну попытку. Если лимит исчерпан, возвращает false и через сколько можно повторить.</summary>
    public bool TryAcquire(string email, out TimeSpan retryAfter)
    {
        using var lease = _limiter.AttemptAcquire(Normalize(email));
        if (lease.IsAcquired)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }

        // Наш лимитер всегда сообщает точное время; запасное значение — только страховка.
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromMinutes(1);
        return false;
    }

    /// <summary>Регистр и пробелы по краям не должны давать обойти лимит («A@x.com» и «a@x.com » — один адрес).</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    public void Dispose() => _limiter.Dispose();
}
