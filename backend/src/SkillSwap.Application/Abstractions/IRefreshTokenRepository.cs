using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Abstractions;

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default);

    void Add(RefreshToken token);

    /// <summary>Отзывает все активные refresh-токены пользователя (все его сеансы).</summary>
    Task RevokeAllForUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Действующие сеансы пользователя: по одному (последнему) токену на сеанс, новые сверху.</summary>
    Task<IReadOnlyList<RefreshToken>> ListActiveByUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Завершает один сеанс пользователя. Возвращает 0, если такого действующего сеанса у этого пользователя нет
    /// (чужой, уже завершённый, истёкший) — отличить эти случаи нельзя намеренно.
    /// </summary>
    Task<int> RevokeSessionAsync(Guid userId, Guid sessionId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Завершает все сеансы пользователя, кроме указанного. Возвращает число завершённых.</summary>
    Task<int> RevokeAllExceptSessionAsync(Guid userId, Guid keepSessionId, DateTime nowUtc, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Сохраняет ротацию. Возвращает false, если параллельный запрос успел раньше (конфликт версий или тот же преемник уже записан):
    /// изменения отброшены, нужно перечитать токен и ответить по льготному окну.
    /// </summary>
    Task<bool> TrySaveRotationAsync(CancellationToken ct = default);
}
