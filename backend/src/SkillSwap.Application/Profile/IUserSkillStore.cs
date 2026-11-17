using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Profile;

public sealed record UserSkillRow(
    SkillDirection Direction,
    Guid SkillId,
    Guid CategoryId,
    string NameTg,
    string NameRu,
    string NameEn,
    SkillLevel Level,
    string? Description,
    bool IsActive);

public sealed record DesiredSkill(SkillDirection Direction, Guid SkillId, SkillLevel Level, string? Description);

/// <param name="IsComplete">После сохранения в каждом направлении есть включённый навык.</param>
/// <param name="IsSearchable">Видимость в подборе после сохранения (при неполной анкете хранилище выключает её).</param>
public sealed record SaveSkillsOutcome(bool Saved, bool IsComplete, bool IsSearchable);

public interface IUserSkillStore
{
    /// <summary>Вся анкета пользователя, включая навыки, выключенные админом. Порядок стабильный: направление, название.</summary>
    Task<IReadOnlyList<UserSkillRow>> ListAsync(Guid userId, CancellationToken ct);

    /// <summary>Только включённые навыки (то, что видят другие).</summary>
    Task<IReadOnlyList<UserSkillRow>> ListActiveAsync(Guid userId, CancellationToken ct);

    /// <summary>Из указанных id оставляет существующие и включённые (сам навык и его категория).</summary>
    Task<HashSet<Guid>> FindActiveSkillIdsAsync(IReadOnlyCollection<Guid> skillIds, CancellationToken ct);

    Task<bool> IsAnketaCompleteAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// Заменяет анкету одной транзакцией под блокировкой строки пользователя. Если после замены анкета неполная,
    /// пользователь скрывается из подбора. <c>Saved: false</c> — параллельное изменение, повторить.
    /// </summary>
    Task<SaveSkillsOutcome> ReplaceAsync(Guid userId, IReadOnlyList<DesiredSkill> desired, CancellationToken ct);
}
