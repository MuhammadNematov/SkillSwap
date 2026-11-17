using SkillSwap.Domain.Common;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Refresh-токен (раздел 10.1 ТЗ). В БД хранится только хеш; сам токен знает лишь клиент (cookie).
/// При каждом обновлении токен меняется на новый (ротация), старый помечается отозванным.
/// Цепочка ротаций одного входа — это один сеанс: у всех её токенов общий <see cref="SessionId"/>.
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

    /// <summary>Хеш токена, который пришёл на смену этому (при ротации). Пусто у токенов, отозванных иначе.</summary>
    public string? ReplacedByTokenHash { get; private set; }

    /// <summary>
    /// Стабильный идентификатор сеанса: не меняется при ротации (в отличие от Id токена),
    /// поэтому по нему можно показывать и завершать сеансы. Лежит в access-токене (claim sid).
    /// </summary>
    public Guid SessionId { get; private set; }

    /// <summary>Когда сеанс начался (вход). При ротации копируется; «последняя активность» — CreatedAt текущего токена.</summary>
    public DateTime SessionStartedAt { get; private set; }

    public string? DeviceInfo { get; private set; }
    public string? IpAddress { get; private set; }

    public static RefreshToken Create(
        Guid userId,
        string tokenHash,
        DateTime expiresAt,
        string? deviceInfo,
        string? ipAddress,
        Guid sessionId,
        DateTime sessionStartedAt)
    {
        return new RefreshToken
        {
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            DeviceInfo = deviceInfo,
            IpAddress = ipAddress,
            SessionId = sessionId,
            SessionStartedAt = sessionStartedAt,
        };
    }

    public bool IsRevoked => RevokedAt is not null;

    /// <summary>Токен заменён новым при ротации (в отличие от выхода или завершения сеанса).</summary>
    public bool WasRotated => ReplacedByTokenHash is not null;

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
