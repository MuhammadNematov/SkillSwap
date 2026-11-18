using System.Collections.Concurrent;

namespace SkillSwap.Application.Common;

/// <summary>Смещение часового пояса IANA от UTC в минутах на заданный момент (с учётом перехода на летнее время).</summary>
public static class TimeZoneOffsets
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Zones = new(StringComparer.Ordinal);

    /// <summary>Неизвестный пояс считается UTC: в подборе он не должен ронять запрос.</summary>
    public static int Minutes(string zoneId, DateTimeOffset utcNow)
    {
        var zone = Zones.GetOrAdd(zoneId, id =>
            TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found) ? found : TimeZoneInfo.Utc);
        return (int)zone.GetUtcOffset(utcNow).TotalMinutes;
    }
}
