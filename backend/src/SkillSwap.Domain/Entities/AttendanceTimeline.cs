namespace SkillSwap.Domain.Entities;

/// <summary>Один восстановленный интервал присутствия: вошёл и (если уже вышел) вышел.</summary>
public readonly record struct AttendanceSpan(DateTime JoinedAt, DateTime? LeftAt);

/// <summary>
/// Присутствие участника, восстановленное из журнала событий комнаты (раздел 6.3 ТЗ). Журнал — единственный источник правды, а не
/// «состояние, которое двигают события»: интервалы каждый раз строятся заново и поэтому не зависят от порядка доставки и повторов.
/// Правила: <c>participant_joined</c> открывает интервал, если нет открытого (повтор входа без выхода игнорируется);
/// <c>participant_left</c> закрывает открытый (выход без входа игнорируется); <c>room_finished</c> закрывает открытые интервалы всех.
/// События идут по времени LiveKit, при равенстве (время в секундах) — по порядку приёма; при совсем равных вход считается раньше выхода.
/// </summary>
public static class AttendanceTimeline
{
    public const string Joined = "participant_joined";
    public const string Left = "participant_left";
    public const string RoomFinished = "room_finished";

    /// <summary>Нужно ли событие этого типа для сборки присутствия.</summary>
    public static bool IsPresenceEvent(string type) => type is Joined or Left or RoomFinished;

    public static IReadOnlyList<AttendanceSpan> Build(IEnumerable<LiveKitWebhookEvent> roomEvents, string identity)
    {
        var relevant = roomEvents
            .Where(e => e.Type == RoomFinished
                || (e.Type is Joined or Left && string.Equals(e.ParticipantIdentity, identity, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.CreatedAt)
            .ThenBy(e => e.Type == Joined ? 0 : 1)
            .ToList();

        var spans = new List<AttendanceSpan>();
        DateTime? open = null;
        foreach (var e in relevant)
        {
            if (e.Type == Joined)
            {
                open ??= e.OccurredAt;
            }
            else if (open is { } joinedAt)
            {
                spans.Add(new AttendanceSpan(joinedAt, e.OccurredAt < joinedAt ? joinedAt : e.OccurredAt));
                open = null;
            }
        }

        if (open is { } stillIn)
        {
            spans.Add(new AttendanceSpan(stillIn, null));
        }

        return spans;
    }
}
