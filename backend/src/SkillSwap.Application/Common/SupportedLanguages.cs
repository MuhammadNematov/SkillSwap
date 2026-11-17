using System.Globalization;

namespace SkillSwap.Application.Common;

/// <summary>Языки интерфейса (tg, ru, en) и выбор языка по заголовку Accept-Language (FR-2).</summary>
public static class SupportedLanguages
{
    public const string Default = "en";

    public static readonly IReadOnlyList<string> All = ["tg", "ru", "en"];

    public static bool IsSupported(string? code) => code is not null && All.Contains(code);

    /// <summary>Берёт язык с наибольшим весом (q), который мы поддерживаем; иначе английский.</summary>
    public static string FromAcceptLanguage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return Default;
        }

        var candidates = header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((part, index) => (Part: part, Index: index))
            .Select(x =>
            {
                var pieces = x.Part.Split(';', StringSplitOptions.TrimEntries);
                var tag = pieces[0];
                var q = 1.0;
                foreach (var param in pieces.Skip(1))
                {
                    if (param.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(param[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        q = parsed;
                    }
                }

                var primary = tag.Split('-')[0].ToLowerInvariant();
                return (Primary: primary, Q: q, x.Index);
            })
            .Where(c => c.Q > 0 && IsSupported(c.Primary))
            .OrderByDescending(c => c.Q)
            .ThenBy(c => c.Index)
            .ToList();

        return candidates.Count > 0 ? candidates[0].Primary : Default;
    }
}
