using System.Text.RegularExpressions;

namespace SkillSwap.Application.Common;

/// <summary>Часовые пояса IANA («Asia/Dushanbe», «Europe/Moscow», «UTC»), раздел 5.2 ТЗ.</summary>
public static partial class TimeZones
{
    public const int MaxLength = 64;

    // Форма идентификатора. Windows-названия («Tajikistan Standard Time») и произвольный текст отсекаются сразу.
    [GeneratedRegex(@"^(UTC|[A-Z][A-Za-z_]*(/[A-Z0-9][A-Za-z0-9_+\-]*)+)$")]
    private static partial Regex IanaShape();

    /// <summary>
    /// Пояс существует в системе и записан ровно так, как его называет система (регистр тоже): иначе на Windows
    /// «asia/dushanbe» нашёлся бы, а на Linux нет, и один и тот же запрос вёл бы себя по-разному.
    /// </summary>
    public static bool IsValid(string? id) =>
        id is { Length: > 0 and <= MaxLength }
        && IanaShape().IsMatch(id)
        && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)
        && string.Equals(zone.Id, id, StringComparison.Ordinal);
}
