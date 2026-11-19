using SkillSwap.Domain.Common;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Один интервал присутствия участника в комнате урока (раздел 13 ТЗ, таблица SessionAttendances): вошёл и вышел.
/// При переподключениях интервалов несколько. Интервал без <see cref="LeftAt"/> открыт: участник сейчас в комнате.
/// Хранятся только время входа и выхода, потоки не записываются (раздел 7.2 ТЗ). Время в UTC, по часам LiveKit.
/// </summary>
public class SessionAttendance : BaseEntity
{
    private SessionAttendance()
    {
    }

    public Guid SessionId { get; private set; }
    public Guid UserId { get; private set; }
    public DateTime JoinedAt { get; private set; }
    public DateTime? LeftAt { get; private set; }

    public static SessionAttendance Open(Guid sessionId, Guid userId, DateTime joinedAt) =>
        new() { SessionId = sessionId, UserId = userId, JoinedAt = joinedAt };

    /// <summary>Закрывает интервал. Выход раньше входа (расхождение часов, перестановка событий) невозможен: время подтягивается ко входу.</summary>
    public void Close(DateTime leftAt)
    {
        if (LeftAt is not null)
        {
            throw new InvalidStateTransitionException(nameof(SessionAttendance), "Closed", "Closed");
        }

        LeftAt = leftAt < JoinedAt ? JoinedAt : leftAt;
    }

    /// <summary>Сколько времени интервал пересекается с другим внутри окна <c>[from, to]</c>; открытый интервал считается до <c>to</c>.</summary>
    public static TimeSpan Overlap(SessionAttendance a, SessionAttendance b, DateTime from, DateTime to)
    {
        var start = Max(Max(a.JoinedAt, b.JoinedAt), from);
        var end = Min(Min(a.LeftAt ?? to, b.LeftAt ?? to), to);
        return end > start ? end - start : TimeSpan.Zero;
    }

    /// <summary>Сколько минут оба участника были в комнате одновременно в окне <c>[from, to]</c>: сумма пересечений всех их интервалов.</summary>
    public static int JointMinutes(IEnumerable<SessionAttendance> all, Guid userA, Guid userB, DateTime from, DateTime to)
    {
        var list = all.ToList();
        var total = TimeSpan.Zero;
        foreach (var a in list.Where(x => x.UserId == userA))
        {
            foreach (var b in list.Where(x => x.UserId == userB))
            {
                total += Overlap(a, b, from, to);
            }
        }

        return (int)total.TotalMinutes;
    }

    private static DateTime Max(DateTime x, DateTime y) => x > y ? x : y;
    private static DateTime Min(DateTime x, DateTime y) => x < y ? x : y;
}
