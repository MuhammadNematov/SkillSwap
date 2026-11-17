using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>Единые правила для названий и синонимов каталога: обрезка пробелов, длина, регистр синонимов.</summary>
internal static class SkillNames
{
    public static string Normalize(string? value, int maxLength)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            throw new DomainValidationException("name_required", "Название не может быть пустым.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainValidationException("name_too_long", $"Название длиннее {maxLength} символов.");
        }

        return trimmed;
    }

    public static string[] NormalizeSynonyms(IEnumerable<string>? synonyms, int maxCount, int maxLength)
    {
        var result = new List<string>();
        foreach (var raw in synonyms ?? [])
        {
            var value = raw?.Trim().ToLowerInvariant() ?? string.Empty;
            if (value.Length == 0 || result.Contains(value))
            {
                continue;
            }

            if (value.Length > maxLength)
            {
                throw new DomainValidationException("synonym_too_long", $"Синоним длиннее {maxLength} символов.");
            }

            result.Add(value);
        }

        if (result.Count > maxCount)
        {
            throw new DomainValidationException("too_many_synonyms", $"Синонимов больше {maxCount}.");
        }

        return [.. result];
    }
}
