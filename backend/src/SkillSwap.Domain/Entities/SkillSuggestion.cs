using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Предложение нового навыка от пользователя (таблица SkillSuggestions, раздел 13 ТЗ). Разбирает админ (этап 7):
/// одобряет (создаёт навык), объединяет с существующим или отклоняет.
/// </summary>
public class SkillSuggestion : BaseEntity
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 100;

    /// <summary>Сколько неразобранных предложений может быть у одного пользователя: защита очереди админа от захламления.</summary>
    public const int MaxPendingPerUser = 10;

    private SkillSuggestion()
    {
    }

    public Guid UserId { get; private set; }

    /// <summary>Название как написал пользователь (пробелы по краям убраны, внутри сведены к одному).</summary>
    public string SuggestedName { get; private set; } = string.Empty;

    /// <summary>Название для сравнения: в нижнем регистре. По нему ищутся повторы.</summary>
    public string NormalizedName { get; private set; } = string.Empty;

    public Guid CategoryId { get; private set; }
    public SuggestionStatus Status { get; private set; } = SuggestionStatus.Pending;

    /// <summary>Навык, созданный по предложению (Approved) или с которым оно объединено (Merged).</summary>
    public Guid? ResolvedSkillId { get; private set; }

    public Guid? ResolvedById { get; private set; }

    /// <summary>Приводит ввод к единому виду: лишние пробелы убраны. Проверяет длину и что в названии есть буква.</summary>
    public static string CleanName(string? name)
    {
        var collapsed = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (collapsed.Length < NameMinLength || collapsed.Length > NameMaxLength)
        {
            throw new DomainValidationException("invalid_name", $"Название: от {NameMinLength} до {NameMaxLength} символов.");
        }

        if (collapsed.Any(char.IsControl))
        {
            throw new DomainValidationException("invalid_name", "В названии есть недопустимые символы.");
        }

        if (!collapsed.Any(char.IsLetter))
        {
            throw new DomainValidationException("invalid_name", "В названии должна быть хотя бы одна буква.");
        }

        return collapsed;
    }

    public static string Normalize(string cleanName) => cleanName.ToLowerInvariant();

    /// <summary>Причина отказа необязательна, но не длиннее.</summary>
    public const int RejectReasonMaxLength = 500;

    /// <summary>Админ одобрил: по предложению создан навык <paramref name="skillId"/> (Pending → Approved).</summary>
    public void Approve(Guid adminId, Guid skillId) => Resolve(SuggestionStatus.Approved, adminId, skillId);

    /// <summary>Админ объединил с уже существующим навыком <paramref name="skillId"/> (Pending → Merged).</summary>
    public void Merge(Guid adminId, Guid skillId) => Resolve(SuggestionStatus.Merged, adminId, skillId);

    /// <summary>Админ отклонил (Pending → Rejected).</summary>
    public void Reject(Guid adminId) => Resolve(SuggestionStatus.Rejected, adminId, null);

    private void Resolve(SuggestionStatus to, Guid adminId, Guid? skillId)
    {
        if (Status != SuggestionStatus.Pending)
        {
            throw new InvalidStateTransitionException(nameof(SkillSuggestion), Status.ToString(), to.ToString());
        }

        Status = to;
        ResolvedById = adminId;
        ResolvedSkillId = skillId;
    }

    public static SkillSuggestion Create(Guid userId, Guid categoryId, string? name)
    {
        var clean = CleanName(name);
        return new SkillSuggestion
        {
            UserId = userId,
            CategoryId = categoryId,
            SuggestedName = clean,
            NormalizedName = Normalize(clean),
            Status = SuggestionStatus.Pending,
        };
    }
}
