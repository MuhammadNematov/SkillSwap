using SkillSwap.Domain.Common;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Резервный код 2FA (раздел 10.1 ТЗ). В БД хранится только хеш, сам код показывается пользователю один раз.
/// Код одноразовый: после использования проставляется <see cref="UsedAt"/>.
/// </summary>
public class TwoFactorRecoveryCode : BaseEntity
{
    private TwoFactorRecoveryCode()
    {
    }

    public Guid UserId { get; private set; }
    public string CodeHash { get; private set; } = string.Empty;
    public DateTime? UsedAt { get; private set; }

    public static TwoFactorRecoveryCode Create(Guid userId, string codeHash) =>
        new() { UserId = userId, CodeHash = codeHash };
}
