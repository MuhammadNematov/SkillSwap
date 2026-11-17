using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Profile;

public interface IProfileStore
{
    /// <summary>Пользователь для чтения (без отслеживания изменений).</summary>
    Task<User?> FindUserAsync(Guid id, CancellationToken ct);

    /// <summary>Пользователь для изменения (с отслеживанием).</summary>
    Task<User?> FindUserForUpdateAsync(Guid id, CancellationToken ct);

    /// <summary>Языки занятий, по алфавиту.</summary>
    Task<string[]> GetLanguagesAsync(Guid userId, CancellationToken ct);

    /// <summary>Сохраняет изменения пользователя и заменяет его языки одной транзакцией. false — параллельное изменение, повторить.</summary>
    Task<bool> SaveProfileAsync(User user, IReadOnlyCollection<string> languages, CancellationToken ct);
}
