using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Навык в анкете пользователя (таблица UserSkills, раздел 13 ТЗ): «Умею» (с описанием опыта) или «Хочу изучать»
/// (с текущим уровнем). Один навык можно добавить в каждое направление только один раз.
/// </summary>
public class UserSkill : BaseEntity
{
    public const int DescriptionMaxLength = 500;

    /// <summary>Анкета заполнена, когда в каждом направлении есть хотя бы столько навыков (раздел 5): подбор открывается после этого.</summary>
    public const int MinPerDirectionForCompleteAnketa = 1;

    /// <summary>Защита от мусора: больше навыков в одном направлении не принимается.</summary>
    public const int MaxPerDirection = 30;

    private UserSkill()
    {
    }

    public Guid UserId { get; private set; }
    public Guid SkillId { get; private set; }
    public Skill? Skill { get; private set; }
    public SkillDirection Direction { get; private set; }
    public SkillLevel Level { get; private set; }
    public string? Description { get; private set; }

    public static UserSkill Create(Guid userId, Guid skillId, SkillDirection direction, SkillLevel level, string? description)
    {
        var skill = new UserSkill { UserId = userId, SkillId = skillId, Direction = direction };
        skill.Update(level, description);
        return skill;
    }

    public void Update(SkillLevel level, string? description)
    {
        if (!Enum.IsDefined(level))
        {
            throw new DomainValidationException("invalid_level", "Неизвестный уровень.");
        }

        var trimmed = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmed is { Length: > DescriptionMaxLength })
        {
            throw new DomainValidationException("description_too_long", $"Описание длиннее {DescriptionMaxLength} символов.");
        }

        Level = level;
        Description = trimmed;
    }
}
