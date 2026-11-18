using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Profile;

public interface IProfileStore
{
    /// <summary>Пользователь для чтения (без отслеживания изменений).</summary>
    Task<User?> FindUserAsync(Guid id, CancellationToken ct);

    /// <summary>Пользователь для изменения (с отслеживанием).</summary>
    Task<User?> FindUserForUpdateAsync(Guid id, CancellationToken ct);

    /// <summary>Записывает адрес аватара (null — убрать). Меняет только это поле.</summary>
    Task SetAvatarUrlAsync(Guid userId, string? avatarUrl, CancellationToken ct);

    /// <summary>Языки занятий, по алфавиту.</summary>
    Task<string[]> GetLanguagesAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Сохраняет изменения пользователя и заменяет его языки одной транзакцией. Если пользователь хочет быть виден
    /// в подборе (<c>IsSearchable</c>), а анкета неполная, ничего не сохраняется (<see cref="ProfileSaveResult.SkillsIncomplete"/>).
    /// </summary>
    Task<ProfileSaveResult> SaveProfileAsync(User user, IReadOnlyCollection<string> languages, CancellationToken ct);
}

public enum ProfileSaveResult
{
    Saved,

    /// <summary>Параллельное изменение: повторить.</summary>
    Conflict,

    /// <summary>Показываться в подборе можно только с заполненной анкетой.</summary>
    SkillsIncomplete,
}
