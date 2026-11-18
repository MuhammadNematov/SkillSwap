using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Статусная модель урока и правила времени (разделы 6.2, 6.3 и 9.3 ТЗ).</summary>
public class LessonSessionTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ExchangeEnd = Now.AddDays(30);
    private static readonly Guid Exchange = Guid.NewGuid();
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static LessonSession Proposed(DateTime? start = null, Guid? by = null) =>
        LessonSession.Propose(Exchange, A, by ?? A, start ?? Now.AddHours(5), "  Python  ", Now, ExchangeEnd);

    private static LessonSession Scheduled(DateTime? start = null)
    {
        var s = Proposed(start);
        s.Confirm(B, Now);
        return s;
    }

    // ---------- предложение ----------

    [Fact]
    public void A_proposal_lasts_45_minutes_and_waits_for_an_answer()
    {
        var s = Proposed(Now.AddHours(5));

        Assert.Equal(SessionStatus.Proposed, s.Status);
        Assert.Equal(Now.AddHours(5).AddMinutes(45), s.EndAt);
        Assert.Equal(45, s.DurationMin);
        Assert.Equal("Python", s.Topic);
        Assert.Equal(A, s.ProposedById);
        Assert.Null(s.PreviousStartAt);
    }

    [Fact]
    public void A_slot_needs_at_least_one_hour_of_notice_exactly_one_hour_is_fine()
    {
        Assert.Equal("slot_too_soon", Assert.Throws<DomainValidationException>(() => Proposed(Now.AddHours(1).AddTicks(-1))).Code);
        Assert.Equal("slot_too_soon", Assert.Throws<DomainValidationException>(() => Proposed(Now.AddMinutes(-5))).Code);
        Assert.NotNull(Proposed(Now.AddHours(1)));
    }

    [Fact]
    public void A_slot_cannot_start_after_the_exchange_ends_but_the_end_moment_itself_is_allowed()
    {
        Assert.Equal("slot_after_exchange_end", Assert.Throws<DomainValidationException>(() => Proposed(ExchangeEnd.AddTicks(1))).Code);
        Assert.NotNull(Proposed(ExchangeEnd));
    }

    [Fact]
    public void A_blank_topic_is_stored_as_null_and_a_long_one_is_refused()
    {
        Assert.Null(LessonSession.Propose(Exchange, A, A, Now.AddHours(3), "  ", Now, ExchangeEnd).Topic);
        Assert.NotNull(LessonSession.Propose(Exchange, A, A, Now.AddHours(3), new string('я', 200), Now, ExchangeEnd));
        Assert.Equal("topic_too_long", Assert.Throws<DomainValidationException>(
            () => LessonSession.Propose(Exchange, A, A, Now.AddHours(3), new string('я', 201), Now, ExchangeEnd)).Code);
    }

    // ---------- ответ ----------

    [Fact]
    public void The_partner_confirms_and_the_session_becomes_scheduled()
    {
        var s = Proposed();

        s.Confirm(B, Now.AddMinutes(10));

        Assert.Equal(SessionStatus.Scheduled, s.Status);
    }

    [Fact]
    public void The_author_cannot_answer_their_own_proposal()
    {
        var s = Proposed();

        Assert.Equal("not_session_answerer", Assert.Throws<DomainValidationException>(() => s.Confirm(A, Now)).Code);
        Assert.Equal("not_session_answerer", Assert.Throws<DomainValidationException>(() => s.Decline(A)).Code);
        Assert.Equal(SessionStatus.Proposed, s.Status);
    }

    [Fact]
    public void A_proposal_cannot_be_confirmed_once_the_start_has_come()
    {
        var s = Proposed(Now.AddHours(2));

        Assert.Equal("session_already_started", Assert.Throws<DomainValidationException>(() => s.Confirm(B, Now.AddHours(2))).Code);
    }

    [Fact]
    public void Declining_closes_the_proposal()
    {
        var s = Proposed();

        s.Decline(B);

        Assert.Equal(SessionStatus.Declined, s.Status);
        Assert.Throws<InvalidStateTransitionException>(() => s.Confirm(B, Now));
        Assert.Throws<InvalidStateTransitionException>(() => s.Decline(B));
    }

    [Fact]
    public void An_unanswered_proposal_expires_exactly_when_it_starts()
    {
        var s = Proposed(Now.AddHours(3));

        Assert.False(s.ExpireIfDue(Now.AddHours(3).AddTicks(-1)));
        Assert.True(s.ExpireIfDue(Now.AddHours(3)));
        Assert.Equal(SessionStatus.Expired, s.Status);
        Assert.False(s.ExpireIfDue(Now.AddHours(4)));   // повтор ничего не меняет
    }

    [Fact]
    public void Only_proposals_expire()
    {
        var s = Scheduled();

        Assert.False(s.ExpireIfDue(Now.AddDays(1)));
        Assert.Equal(SessionStatus.Scheduled, s.Status);
    }

    // ---------- перенос ----------

    [Fact]
    public void Rescheduling_returns_to_proposed_with_the_new_time_and_forgets_confirmations()
    {
        var s = Scheduled(Now.AddHours(10));
        var newTime = Now.AddHours(30);

        s.Reschedule(B, newTime, Now.AddHours(1), ExchangeEnd);

        Assert.Equal(SessionStatus.Proposed, s.Status);
        Assert.Equal(newTime, s.StartAt);
        Assert.Equal(newTime.AddMinutes(45), s.EndAt);
        Assert.Equal(Now.AddHours(10), s.PreviousStartAt);
        Assert.Equal(B, s.ProposedById);   // отвечать теперь должен тот, кто не предлагал перенос
        Assert.False(s.Reminder24Sent);
    }

    [Fact]
    public void Rescheduling_is_free_up_to_two_hours_before_the_start_and_impossible_later()
    {
        var start = Now.AddHours(10);

        var ok = Scheduled(start);
        ok.Reschedule(A, Now.AddHours(20), start.AddHours(-2), ExchangeEnd);   // ровно за 2 часа можно
        Assert.Equal(SessionStatus.Proposed, ok.Status);

        var late = Scheduled(start);
        Assert.Equal("reschedule_too_late", Assert.Throws<DomainValidationException>(
            () => late.Reschedule(A, Now.AddHours(20), start.AddHours(-2).AddTicks(1), ExchangeEnd)).Code);
        Assert.Equal(SessionStatus.Scheduled, late.Status);
    }

    [Fact]
    public void The_new_time_follows_the_same_rules_as_a_proposal_and_failure_changes_nothing()
    {
        var s = Scheduled(Now.AddHours(10));

        Assert.Equal("slot_too_soon", Assert.Throws<DomainValidationException>(() => s.Reschedule(A, Now.AddMinutes(30), Now, ExchangeEnd)).Code);
        Assert.Equal("slot_after_exchange_end", Assert.Throws<DomainValidationException>(() => s.Reschedule(A, ExchangeEnd.AddDays(1), Now, ExchangeEnd)).Code);
        Assert.Equal(SessionStatus.Scheduled, s.Status);
        Assert.Equal(Now.AddHours(10), s.StartAt);
    }

    [Fact]
    public void Only_a_scheduled_session_can_be_rescheduled()
    {
        Assert.Throws<InvalidStateTransitionException>(() => Proposed().Reschedule(A, Now.AddHours(20), Now, ExchangeEnd));
    }

    // ---------- отмена ----------

    [Fact]
    public void The_author_withdraws_a_proposal_and_the_partner_cannot()
    {
        var s = Proposed();

        Assert.Equal("not_session_proposer", Assert.Throws<DomainValidationException>(() => s.Cancel(B, Now)).Code);
        s.Cancel(A, Now);

        Assert.Equal(SessionStatus.Cancelled, s.Status);
        Assert.Equal(A, s.CancelledById);
    }

    [Fact]
    public void Cancelling_two_hours_or_more_before_the_start_is_free_and_later_is_late()
    {
        var start = Now.AddHours(10);

        var free = Scheduled(start);
        free.Cancel(B, start.AddHours(-2));
        Assert.Equal(SessionStatus.Cancelled, free.Status);
        Assert.Equal(B, free.CancelledById);

        var late = Scheduled(start);
        late.Cancel(B, start.AddHours(-2).AddTicks(1));
        Assert.Equal(SessionStatus.LateCancelled, late.Status);

        var veryLate = Scheduled(start);
        veryLate.Cancel(A, start.AddMinutes(-1));
        Assert.Equal(SessionStatus.LateCancelled, veryLate.Status);
    }

    [Fact]
    public void A_started_session_cannot_be_cancelled()
    {
        var s = Scheduled(Now.AddHours(10));

        Assert.Equal("session_already_started", Assert.Throws<DomainValidationException>(() => s.Cancel(A, Now.AddHours(10))).Code);
    }

    [Fact]
    public void A_finished_session_cannot_be_cancelled()
    {
        var declined = Proposed();
        declined.Decline(B);
        Assert.Throws<InvalidStateTransitionException>(() => declined.Cancel(A, Now));

        var cancelled = Scheduled();
        cancelled.Cancel(A, Now);
        Assert.Throws<InvalidStateTransitionException>(() => cancelled.Cancel(B, Now));
    }

    [Fact]
    public void An_aborted_or_frozen_exchange_cancels_open_sessions_without_a_culprit()
    {
        var proposed = Proposed();
        var scheduled = Scheduled();
        var declined = Proposed();
        declined.Decline(B);

        Assert.True(proposed.CancelByExchange());
        Assert.True(scheduled.CancelByExchange());
        Assert.False(declined.CancelByExchange());   // уже закрытый не трогается

        Assert.Equal(SessionStatus.Cancelled, proposed.Status);
        Assert.Equal(SessionStatus.Cancelled, scheduled.Status);
        Assert.Null(scheduled.CancelledById);
        Assert.False(scheduled.CancelByExchange());   // повтор ничего не меняет
    }

    // ---------- после урока (до видеозвонков) ----------

    [Fact]
    public void After_the_planned_end_a_scheduled_session_becomes_unconfirmed()
    {
        var s = Scheduled(Now.AddHours(5));

        Assert.False(s.MarkUnconfirmedIfDue(s.EndAt.AddTicks(-1)));
        Assert.True(s.MarkUnconfirmedIfDue(s.EndAt));

        Assert.Equal(SessionStatus.Unconfirmed, s.Status);
        Assert.Equal(s.EndAt, s.UnconfirmedAt);
        Assert.Equal(s.EndAt.AddHours(24), s.ConfirmDeadline);
        Assert.False(s.MarkUnconfirmedIfDue(s.EndAt.AddHours(1)));
    }

    [Fact]
    public void Only_scheduled_sessions_become_unconfirmed()
    {
        var s = Proposed(Now.AddHours(5));

        Assert.False(s.MarkUnconfirmedIfDue(Now.AddDays(1)));
        Assert.Equal(SessionStatus.Proposed, s.Status);
    }

    [Fact]
    public void Both_pressing_the_lesson_took_place_completes_it_and_repeats_are_harmless()
    {
        var s = Scheduled(Now.AddHours(5));
        s.MarkUnconfirmedIfDue(s.EndAt);

        Assert.False(s.ConfirmManually(A, isParticipantA: true, s.EndAt.AddHours(1)));
        Assert.False(s.ConfirmManually(A, isParticipantA: true, s.EndAt.AddHours(2)));
        Assert.Equal(SessionStatus.Unconfirmed, s.Status);
        Assert.True(s.ConfirmManually(B, isParticipantA: false, s.EndAt.AddHours(3)));

        Assert.Equal(SessionStatus.Completed, s.Status);
        Assert.Throws<InvalidStateTransitionException>(() => s.ConfirmManually(A, true, s.EndAt.AddHours(4)));
    }

    [Fact]
    public void Manual_confirmation_closes_after_24_hours()
    {
        var s = Scheduled(Now.AddHours(5));
        s.MarkUnconfirmedIfDue(s.EndAt);

        Assert.False(s.ConfirmManually(A, true, s.EndAt.AddHours(24)));   // ровно 24 часа ещё можно
        Assert.Equal("confirm_window_closed", Assert.Throws<DomainValidationException>(
            () => s.ConfirmManually(B, false, s.EndAt.AddHours(24).AddTicks(1))).Code);
    }

    [Fact]
    public void Without_a_double_confirmation_the_session_is_not_counted_after_24_hours()
    {
        var s = Scheduled(Now.AddHours(5));
        s.MarkUnconfirmedIfDue(s.EndAt);
        s.ConfirmManually(A, true, s.EndAt.AddHours(2));   // подтвердил только один

        Assert.False(s.ExpireUnconfirmedIfDue(s.EndAt.AddHours(24).AddTicks(-1)));
        Assert.True(s.ExpireUnconfirmedIfDue(s.EndAt.AddHours(24)));

        Assert.Equal(SessionStatus.NotCounted, s.Status);
        Assert.False(s.ExpireUnconfirmedIfDue(s.EndAt.AddDays(5)));
    }

    [Fact]
    public void Manual_confirmation_is_only_for_unconfirmed_sessions()
    {
        Assert.Throws<InvalidStateTransitionException>(() => Scheduled().ConfirmManually(A, true, Now));
        Assert.Throws<InvalidStateTransitionException>(() => Proposed().ConfirmManually(A, true, Now));
    }

    [Fact]
    public void Reminders_are_remembered_per_kind()
    {
        var s = Scheduled();

        s.MarkReminderSent(1440);
        Assert.True(s.Reminder24Sent);
        Assert.False(s.Reminder15Sent);
        s.MarkReminderSent(15);
        Assert.True(s.Reminder15Sent);
    }
}
