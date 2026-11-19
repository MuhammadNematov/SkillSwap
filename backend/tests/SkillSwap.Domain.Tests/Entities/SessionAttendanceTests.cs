using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Интервалы присутствия и «совместное время» — пересечение интервалов двух участников (раздел 6.3 ТЗ).</summary>
public class SessionAttendanceTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static SessionAttendance Interval(Guid user, int fromMin, int? toMin)
    {
        var a = SessionAttendance.Open(Session, user, T0.AddMinutes(fromMin));
        if (toMin is not null)
        {
            a.Close(T0.AddMinutes(toMin.Value));
        }

        return a;
    }

    [Fact]
    public void An_interval_is_open_until_closed_and_cannot_be_closed_twice()
    {
        var a = Interval(A, 0, null);
        Assert.Null(a.LeftAt);

        a.Close(T0.AddMinutes(5));
        Assert.Equal(T0.AddMinutes(5), a.LeftAt);
        Assert.Throws<InvalidStateTransitionException>(() => a.Close(T0.AddMinutes(6)));
    }

    [Fact]
    public void A_leave_before_the_join_is_pulled_to_the_join_time()
    {
        var a = SessionAttendance.Open(Session, A, T0.AddMinutes(10));
        a.Close(T0.AddMinutes(5));

        Assert.Equal(T0.AddMinutes(10), a.LeftAt);
    }

    [Fact]
    public void Joint_time_is_the_overlap_of_both_participants()
    {
        var joint = SessionAttendance.JointMinutes([Interval(A, 0, 40), Interval(B, 10, 50)], A, B, T0, T0.AddMinutes(75));

        Assert.Equal(30, joint);
    }

    [Fact]
    public void Reconnects_add_up_and_time_alone_does_not_count()
    {
        // A: 0–20 и 25–60; B: 10–30 и 40–70. Совместно: 10–20 (10) + 25–30 (5) + 40–60 (20) = 35.
        var all = new[] { Interval(A, 0, 20), Interval(A, 25, 60), Interval(B, 10, 30), Interval(B, 40, 70) };

        Assert.Equal(35, SessionAttendance.JointMinutes(all, A, B, T0, T0.AddMinutes(75)));
    }

    [Fact]
    public void An_open_interval_counts_until_the_end_of_the_window()
    {
        var all = new[] { Interval(A, -5, null), Interval(B, 60, null) };

        Assert.Equal(15, SessionAttendance.JointMinutes(all, A, B, T0, T0.AddMinutes(75)));
    }

    [Fact]
    public void The_window_cuts_time_outside_of_it()
    {
        var all = new[] { Interval(A, -10, 30), Interval(B, -10, 30) };

        Assert.Equal(30, SessionAttendance.JointMinutes(all, A, B, T0, T0.AddMinutes(75)));
        Assert.Equal(20, SessionAttendance.JointMinutes(all, A, B, T0.AddMinutes(10), T0.AddMinutes(75)));
    }

    [Fact]
    public void Nobody_together_means_zero_and_a_third_person_is_ignored()
    {
        var other = Guid.NewGuid();
        var all = new[] { Interval(A, 0, 10), Interval(B, 20, 30), Interval(other, 0, 30) };

        Assert.Equal(0, SessionAttendance.JointMinutes(all, A, B, T0, T0.AddMinutes(75)));
    }
}
