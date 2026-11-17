using SkillSwap.Domain.Common;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Refresh-токен (раздел 10.1 ТЗ). В БД хранится только хеш; сам токен знает лишь клиент (cookie).
/// При каждом обновлении токен меняется на новый (ротация), старый помечается отозванным.
/// </summary>
public class RefreshToken : BaseEntity
{
    private RefreshToken()
    {
    }

    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }

    /// <summary>Хеш токена, который пришёл на смену этому (при ротации).</summary>
    public string? ReplacedByTokenHash { get; private set; }

    public string? DeviceInfo { get; private set; }
    public string? IpAddress { get; private set; }

    public static RefreshToken Create(Guid userId, string tokenHash, DateTime expiresAt, string? deviceInfo, string? ipAddress)
    {
        return new RefreshToken
        {
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            DeviceInfo = deviceInfo,
            IpAddress = ipAddress,
        };
    }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsActive(DateTime nowUtc) => RevokedAt is null && nowUtc < ExpiresAt;

    /// <summary>Отзывает токен. Повторный вызов ничего не меняет.</summary>
    public void Revoke(DateTime nowUtc, string? replacedByTokenHash = null)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = nowUtc;
        ReplacedByTokenHash = replacedByTokenHash;
    }
}
