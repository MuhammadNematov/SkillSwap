namespace SkillSwap.Infrastructure.Persistence;

/// <summary>Шаблон для поиска «содержит» через ILIKE: служебные символы LIKE в запросе пользователя (<c>%</c>, <c>_</c>, <c>\</c>) ищутся как обычные.</summary>
public static class SearchPattern
{
    public const string Escape = "\\";

    public static string Contains(string text) => "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
