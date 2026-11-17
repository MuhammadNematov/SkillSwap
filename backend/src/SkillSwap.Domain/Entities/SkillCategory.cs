using SkillSwap.Domain.Common;

namespace SkillSwap.Domain.Entities;

/// <summary>Категория каталога навыков («IT», «Иностранные языки», «Дизайн»), раздел 5.2 ТЗ. Названия на трёх языках.</summary>
public class SkillCategory : BaseEntity
{
    public const int NameMaxLength = 100;

    private SkillCategory()
    {
    }

    public string NameTg { get; private set; } = string.Empty;
    public string NameRu { get; private set; } = string.Empty;
    public string NameEn { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }

    /// <summary>Выключенную категорию (и её навыки) пользователи не видят; записи не удаляются, чтобы не ломать анкеты.</summary>
    public bool IsActive { get; private set; } = true;

    public List<Skill> Skills { get; private set; } = [];

    public static SkillCategory Create(string nameTg, string nameRu, string nameEn, int sortOrder)
    {
        var category = new SkillCategory();
        category.Update(nameTg, nameRu, nameEn, sortOrder, isActive: true);
        return category;
    }

    public void Update(string nameTg, string nameRu, string nameEn, int sortOrder, bool isActive)
    {
        var tg = SkillNames.Normalize(nameTg, NameMaxLength);
        var ru = SkillNames.Normalize(nameRu, NameMaxLength);
        var en = SkillNames.Normalize(nameEn, NameMaxLength);

        NameTg = tg;
        NameRu = ru;
        NameEn = en;
        SortOrder = sortOrder;
        IsActive = isActive;
    }
}
