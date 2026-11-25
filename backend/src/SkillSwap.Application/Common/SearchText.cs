using SkillSwap.Application.Exceptions;

namespace SkillSwap.Application.Common;

/// <summary>Поисковая строка из параметра <c>q</c> списков: пробелы по краям убираются, пустая строка значит «без поиска», слишком длинная — 400 <c>invalid_search</c>.</summary>
public static class SearchText
{
    public const int MaxLength = 100;

    public static string? Normalize(string? q)
    {
        var text = q?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > MaxLength)
        {
            throw new BadRequestException("invalid_search", $"Поисковая строка не длиннее {MaxLength} символов.");
        }

        return text;
    }
}
