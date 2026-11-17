using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Abstractions;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default);

    void Add(RefreshToken token);

    /// <summary>Отзывает все активные refresh-токены пользователя (все его сеансы).</summary>
    Task RevokeAllForUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
