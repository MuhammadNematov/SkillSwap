using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Статусы урока от видеозвонка: окно комнаты, проверка на 15-й минуте, итог при закрытии (разделы 6.3, 7.2, 9.3 ТЗ).</summary>
public class LessonSessionVideoTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = Now.AddHours(5);
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static LessonSession Scheduled()
    {
        var s = LessonSession.Propose(Guid.NewGuid(), A, A, Start, null, Now, Now.AddDays(30));
        s.Confirm(B, Now);
        return s;
    }

    private static LessonSession InProgress()
    {
        var s = Scheduled();
        s.MarkStarted();
        return s;
    }

    private static Dictionary<Guid, DateTime> Joins(DateTime? a, DateTime? b)
    {
        var d = new Dictionary<Guid, DateTime>();
        if (a is not null)
        {
            d[A] = a.Value;
        }

        if (b is not null)
        {
            d[B] = b.Value;
        }

        return d;
    }

    // ---------- окно комнаты ----------

    [Fact]
    public void The_room_window_is_ten_minutes_before_start_until_start_plus_75_minutes()
    {
        var s = Scheduled();

        Assert.Equal(Start.AddMinutes(-10), s.JoinOpensAt);
        Assert.Equal(Start.AddMinutes(75), s.RoomClosesAt);
        Assert.Equal(Start.AddMinutes(15), s.FirstCheckAt);
    }

    [Fact]
    public void A_token_is_possible_only_inside_the_window_the_opening_moment_is_in_the_closing_moment_is_out()
    {
        var s = Scheduled();

        Assert.False(s.IsJoinableAt(Start.AddMinutes(-10).AddTicks(-1)));
        Assert.True(s.IsJoinableAt(Start.AddMinutes(-10)));
        Assert.True(s.IsJoinableAt(Start.AddMinutes(75).AddTicks(-1)));
        Assert.False(s.IsJoinableAt(Start.AddMinutes(75)));
    }

    [Theory]
    [InlineData(SessionStatus.Proposed, false)]
    [InlineData(SessionStatus.Scheduled, true)]
    [InlineData(SessionStatus.InProgress, true)]
    [InlineData(SessionStatus.NoShow, true)]
    [InlineData(SessionStatus.Missed, true)]
    [InlineData(SessionStatus.Completed, false)]
    [InlineData(SessionStatus.Unconfirmed, false)]
    [InlineData(SessionStatus.Cancelled, false)]
    public void Only_confirmed_and_not_yet_closed_lessons_are_joinable(SessionStatus status, bool joinable)
    {
        var s = Scheduled();
        typeof(LessonSession).GetProperty(nameof(LessonSession.Status))!.SetValue(s, status);

        Assert.Equal(joinable, s.IsJoinableAt(Start));
    }

    // ---------- вход ----------

    [Fact]
    public void The_first_join_starts_the_lesson_and_later_ones_change_nothing()
    {
        var s = Scheduled();

        Assert.True(s.MarkStarted());
        Assert.Equal(SessionStatus.InProgress, s.Status);
        Assert.False(s.MarkStarted());
    }

    [Fact]
    public void A_proposal_that_was_never_confirmed_is_not_started_by_a_join()
    {
        var s = LessonSession.Propose(Guid.NewGuid(), A, A, Start, null, Now, Now.AddDays(30));

        Assert.False(s.MarkStarted());
        Assert.Equal(SessionStatus.Proposed, s.Status);
    }

    // ---------- проверка на 15-й минуте ----------

    [Fact]
    public void The_first_check_does_nothing_before_the_fifteenth_minute()
    {
        var s = InProgress();

        Assert.False(s.ApplyFirstCheck(Start.AddMinutes(15).AddTicks(-1), [A], A, B));
        Assert.Equal(SessionStatus.InProgress, s.Status);
    }

    [Fact]
    public void Nobody_at_the_fifteenth_minute_is_missed()
    {
        var s = Scheduled();

        Assert.True(s.ApplyFirstCheck(Start.AddMinutes(15), [], A, B));
        Assert.Equal(SessionStatus.Missed, s.Status);
        Assert.Null(s.NoShowUserId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void One_participant_at_the_fifteenth_minute_makes_the_other_one_the_no_show(bool aJoined)
    {
        var s = InProgress();

        Assert.True(s.ApplyFirstCheck(Start.AddMinutes(15), [aJoined ? A : B], A, B));
        Assert.Equal(SessionStatus.NoShow, s.Status);
        Assert.Equal(aJoined ? B : A, s.NoShowUserId);
    }

    [Fact]
    public void Both_present_at_the_fifteenth_minute_keeps_the_lesson_in_progress()
    {
        var s = InProgress();

        Assert.False(s.ApplyFirstCheck(Start.AddMinutes(15), [A, B], A, B));
        Assert.Equal(SessionStatus.InProgress, s.Status);
    }

    [Fact]
    public void The_first_check_is_a_one_shot_it_does_not_touch_a_lesson_already_decided()
    {
        var s = InProgress();
        s.ApplyFirstCheck(Start.AddMinutes(15), [A], A, B);

        Assert.False(s.ApplyFirstCheck(Start.AddMinutes(20), [], A, B));
        Assert.Equal(SessionStatus.NoShow, s.Status);
    }

    // ---------- итог при закрытии комнаты ----------

    [Fact]
    public void Thirty_joint_minutes_complete_the_lesson_and_exactly_thirty_is_enough()
    {
        var s = InProgress();

        Assert.True(s.CloseRoom(Start.AddMinutes(75), Joins(Start, Start), 30, A, B));
        Assert.Equal(SessionStatus.Completed, s.Status);
        Assert.Null(s.LateJoinMinutes);
    }

    [Fact]
    public void Twenty_nine_joint_minutes_leave_the_lesson_unconfirmed_for_a_manual_answer()
    {
        var s = InProgress();
        var close = Start.AddMinutes(75);

        Assert.True(s.CloseRoom(close, Joins(Start, Start), 29, A, B));
        Assert.Equal(SessionStatus.Unconfirmed, s.Status);
        Assert.Equal(close, s.UnconfirmedAt);
        Assert.Equal(close.AddHours(24), s.ConfirmDeadline);
    }

    [Fact]
    public void A_late_partner_turns_no_show_into_completed_and_the_lateness_is_kept()
    {
        var s = InProgress();
        s.ApplyFirstCheck(Start.AddMinutes(15), [A], A, B);

        Assert.True(s.CloseRoom(Start.AddMinutes(75), Joins(Start, Start.AddMinutes(20)), 40, A, B));
        Assert.Equal(SessionStatus.Completed, s.Status);
        Assert.Equal(20, s.LateJoinMinutes);
        Assert.Equal(B, s.NoShowUserId);   // кто опоздал, остаётся в записи
    }

    [Fact]
    public void Missed_becomes_completed_when_both_came_late_but_spent_thirty_minutes_together()
    {
        var s = Scheduled();
        s.ApplyFirstCheck(Start.AddMinutes(15), [], A, B);

        Assert.True(s.CloseRoom(Start.AddMinutes(75), Joins(Start.AddMinutes(16), Start.AddMinutes(18)), 30, A, B));
        Assert.Equal(SessionStatus.Completed, s.Status);
        Assert.Equal(18, s.LateJoinMinutes);
    }

    [Fact]
    public void No_show_stays_final_if_the_late_partner_did_not_make_thirty_minutes()
    {
        var s = InProgress();
        s.ApplyFirstCheck(Start.AddMinutes(15), [A], A, B);

        Assert.False(s.CloseRoom(Start.AddMinutes(75), Joins(Start, Start.AddMinutes(50)), 25, A, B));
        Assert.Equal(SessionStatus.NoShow, s.Status);
        Assert.Equal(B, s.NoShowUserId);
    }

    [Fact]
    public void Missed_stays_missed_when_only_one_person_came_late()
    {
        var s = Scheduled();
        s.ApplyFirstCheck(Start.AddMinutes(15), [], A, B);

        Assert.False(s.CloseRoom(Start.AddMinutes(75), Joins(Start.AddMinutes(30), null), 0, A, B));
        Assert.Equal(SessionStatus.Missed, s.Status);
        Assert.Null(s.NoShowUserId);
    }

    [Fact]
    public void If_the_first_check_never_ran_the_final_check_still_decides()
    {
        var one = InProgress();
        Assert.True(one.CloseRoom(Start.AddMinutes(75), Joins(null, Start), 0, A, B));
        Assert.Equal(SessionStatus.NoShow, one.Status);
        Assert.Equal(A, one.NoShowUserId);

        var none = Scheduled();
        Assert.True(none.CloseRoom(Start.AddMinutes(75), Joins(null, null), 0, A, B));
        Assert.Equal(SessionStatus.Missed, none.Status);
    }

    [Fact]
    public void The_final_check_waits_for_the_room_to_close_and_never_runs_twice()
    {
        var s = InProgress();

        Assert.False(s.CloseRoom(Start.AddMinutes(75).AddTicks(-1), Joins(Start, Start), 45, A, B));
        Assert.Equal(SessionStatus.InProgress, s.Status);

        Assert.True(s.CloseRoom(Start.AddMinutes(75), Joins(Start, Start), 45, A, B));
        Assert.False(s.CloseRoom(Start.AddMinutes(80), Joins(Start, Start), 45, A, B));
    }

    [Fact]
    public void Manual_confirmation_counts_the_24_hours_from_the_room_closing()
    {
        var s = InProgress();
        var close = Start.AddMinutes(75);
        s.CloseRoom(close, Joins(Start, Start), 10, A, B);

        Assert.False(s.ConfirmManually(A, true, close.AddHours(23)));
        Assert.True(s.ConfirmManually(B, false, close.AddHours(23)));
        Assert.Equal(SessionStatus.Completed, s.Status);
    }
}
