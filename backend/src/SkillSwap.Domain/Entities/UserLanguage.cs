namespace SkillSwap.Domain.Entities;

/// <summary>Язык, на котором пользователь готов заниматься (таблица UserLanguages, раздел 13 ТЗ). Ключ составной: пользователь + код языка.</summary>
public class UserLanguage
{
    private UserLanguage()
    {
    }

    public Guid UserId { get; private set; }

    /// <summary>Код языка: tg, ru или en.</summary>
    public string LanguageCode { get; private set; } = string.Empty;

    public static UserLanguage Create(Guid userId, string languageCode) =>
        new() { UserId = userId, LanguageCode = languageCode };
}
