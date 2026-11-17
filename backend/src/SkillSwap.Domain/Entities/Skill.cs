using SkillSwap.Domain.Common;

namespace SkillSwap.Domain.Entities;

/// <summary>Навык в каталоге («Python», «Английский язык»): название на трёх языках и синонимы для поиска (раздел 5.2 ТЗ).</summary>
public class Skill : BaseEntity
{
    public const int NameMaxLength = 100;
    public const int MaxSynonyms = 20;
    public const int SynonymMaxLength = 60;

    private Skill()
    {
    }

    public Guid CategoryId { get; private set; }
    public SkillCategory? Category { get; private set; }

    public string NameTg { get; private set; } = string.Empty;
    public string NameRu { get; private set; } = string.Empty;
    public string NameEn { get; private set; } = string.Empty;

    /// <summary>Другие названия для поиска («js» для JavaScript, «питон» для Python). Хранятся в нижнем регистре, без повторов.</summary>
    public string[] Synonyms { get; private set; } = [];

    public bool IsActive { get; private set; } = true;

    public static Skill Create(
        Guid categoryId, string nameTg, string nameRu, string nameEn, IEnumerable<string>? synonyms = null)
    {
        var skill = new Skill { CategoryId = categoryId };
        skill.Update(categoryId, nameTg, nameRu, nameEn, synonyms, isActive: true);
        return skill;
    }

    public void Update(
        Guid categoryId, string nameTg, string nameRu, string nameEn, IEnumerable<string>? synonyms, bool isActive)
    {
        // Сначала проверяем всё, потом присваиваем: при ошибке сущность остаётся прежней.
        var tg = SkillNames.Normalize(nameTg, NameMaxLength);
        var ru = SkillNames.Normalize(nameRu, NameMaxLength);
        var en = SkillNames.Normalize(nameEn, NameMaxLength);
        var normalizedSynonyms = SkillNames.NormalizeSynonyms(synonyms, MaxSynonyms, SynonymMaxLength);

        CategoryId = categoryId;
        NameTg = tg;
        NameRu = ru;
        NameEn = en;
        Synonyms = normalizedSynonyms;
        IsActive = isActive;
    }
}
