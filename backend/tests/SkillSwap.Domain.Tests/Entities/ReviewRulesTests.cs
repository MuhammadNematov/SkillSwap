using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Правила «когда можно оставить отзыв» (раздел 8.1 ТЗ), в том числе прерывание блокировкой, одностороннее право и сроки.</summary>
public class ReviewRulesTests
{
    private static readonly DateTime ClosedAt = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = ClosedAt.AddDays(1);

    private static ReviewContext Ctx(
        ExchangeStatus status = ExchangeStatus.Completed, DateTime? closedAt = null, bool partnerDeleted = false,
        int completed = 1, int mineTaught = 1, int partnerTaught = 0, int partnerNoShows = 0, int myNoShows = 0, bool closed = true) =>
        new(status, closed ? closedAt ?? ClosedAt : null, partnerDeleted, completed, mineTaught, partnerTaught, partnerNoShows, myNoShows);

    // ---------- обмен ещё не закрыт ----------

    [Theory]
    [InlineData(ExchangeStatus.Active)]
    [InlineData(ExchangeStatus.Frozen)]
    public void Nothing_can_be_written_while_the_exchange_is_open_or_frozen(ExchangeStatus status)
    {
        var e = ReviewRules.Evaluate(Ctx(status, closed: false), Now);

        Assert.False(e.CanWrite);
        Assert.Equal(ReviewUnavailable.ExchangeNotClosed, e.Reason);
        Assert.Null(e.DeadlineAt);
    }

    [Fact]
    public void A_closed_status_without_a_closing_time_is_not_closed_either()
    {
        Assert.Equal(ReviewUnavailable.ExchangeNotClosed, ReviewRules.Evaluate(Ctx(closed: false), Now).Reason);
    }

    // ---------- правила 1 и 2: хотя бы одна засчитанная сессия ----------

    [Theory]
    [InlineData(ExchangeStatus.Completed)]
    [InlineData(ExchangeStatus.Aborted)]
    public void One_counted_session_gives_a_review_after_a_normal_end_and_after_an_abort(ExchangeStatus status)
    {
        var e = ReviewRules.Evaluate(Ctx(status, completed: 1), Now);

        Assert.True(e.CanWrite);
        Assert.Null(e.Reason);
        Assert.False(e.OneSided);
        Assert.Equal(ClosedAt.AddDays(14), e.DeadlineAt);
    }

    [Fact]
    public void A_block_does_not_take_the_right_away_when_there_was_a_counted_session_for_either_side()
    {
        // Обмен прерван блокировкой — для правил это обычный Aborted: право сохраняют оба, чтобы нарушитель не мог заблокировать партнёра первым.
        var blocker = ReviewRules.Evaluate(Ctx(ExchangeStatus.Aborted, completed: 2, mineTaught: 1, partnerTaught: 1), Now);
        var blocked = ReviewRules.Evaluate(Ctx(ExchangeStatus.Aborted, completed: 2, mineTaught: 1, partnerTaught: 1), Now);

        Assert.True(blocker.CanWrite);
        Assert.True(blocked.CanWrite);
        Assert.False(blocker.OneSided);
        Assert.False(blocked.OneSided);
    }

    [Fact]
    public void Rule_four_no_counted_sessions_and_no_no_shows_means_no_reviews()
    {
        var e = ReviewRules.Evaluate(Ctx(completed: 0, mineTaught: 0), Now);

        Assert.False(e.CanWrite);
        Assert.Equal(ReviewUnavailable.NoCountedSessions, e.Reason);
        Assert.Equal(ClosedAt.AddDays(14), e.DeadlineAt);
    }

    // ---------- правило 3: одностороннее право ----------

    [Fact]
    public void Three_final_no_shows_of_the_partner_give_the_victim_a_review_alone_even_without_sessions()
    {
        var victim = ReviewRules.Evaluate(Ctx(ExchangeStatus.Aborted, completed: 0, mineTaught: 0, partnerNoShows: 3), Now);
        var culprit = ReviewRules.Evaluate(Ctx(ExchangeStatus.Aborted, completed: 0, mineTaught: 0, partnerNoShows: 0, myNoShows: 3), Now);

        Assert.True(victim.CanWrite);
        Assert.True(victim.OneSided);                       // партнёру писать нельзя: отзыв публикуется сразу
        Assert.False(culprit.CanWrite);                     // неявившемуся право не даётся
        Assert.Equal(ReviewUnavailable.NoCountedSessions, culprit.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Fewer_than_three_no_shows_without_sessions_give_nothing(int noShows)
    {
        var e = ReviewRules.Evaluate(Ctx(completed: 0, mineTaught: 0, partnerNoShows: noShows), Now);

        Assert.False(e.CanWrite);
        Assert.Equal(ReviewUnavailable.NoCountedSessions, e.Reason);
    }

    [Fact]
    public void If_both_were_no_shows_three_times_each_has_the_right_and_neither_is_one_sided()
    {
        var a = ReviewRules.Evaluate(Ctx(completed: 0, mineTaught: 0, partnerNoShows: 3, myNoShows: 3), Now);

        Assert.True(a.CanWrite);
        Assert.False(a.OneSided);   // у партнёра тоже есть право: слепая публикация как обычно
    }

    [Fact]
    public void With_counted_sessions_the_no_shows_change_nothing_both_write_by_the_blind_rules()
    {
        var e = ReviewRules.Evaluate(Ctx(completed: 2, partnerNoShows: 3), Now);

        Assert.True(e.CanWrite);
        Assert.False(e.OneSided);
    }

    [Fact]
    public void The_partner_has_a_right_when_there_were_sessions_or_when_i_was_absent_three_times()
    {
        Assert.True(ReviewRules.PartnerHasRight(Ctx(completed: 1)));
        Assert.True(ReviewRules.PartnerHasRight(Ctx(completed: 0, myNoShows: 3)));
        Assert.False(ReviewRules.PartnerHasRight(Ctx(completed: 0, myNoShows: 2)));
        Assert.False(ReviewRules.PartnerHasRight(Ctx(completed: 0, partnerNoShows: 5)));
    }

    // ---------- удалённый партнёр ----------

    [Fact]
    public void A_review_about_a_deleted_account_cannot_be_written()
    {
        var e = ReviewRules.Evaluate(Ctx(partnerDeleted: true), Now);

        Assert.False(e.CanWrite);
        Assert.Equal(ReviewUnavailable.PartnerDeleted, e.Reason);
    }

    [Fact]
    public void A_deleted_partner_takes_the_right_even_from_a_victim_of_no_shows()
    {
        var e = ReviewRules.Evaluate(Ctx(partnerDeleted: true, completed: 0, mineTaught: 0, partnerNoShows: 3), Now);

        Assert.Equal(ReviewUnavailable.PartnerDeleted, e.Reason);
    }

    // ---------- срок ----------

    [Fact]
    public void The_deadline_is_fourteen_days_after_closing_and_its_last_moment_is_included()
    {
        var onTheLastMoment = ReviewRules.Evaluate(Ctx(), ClosedAt.AddDays(14));
        var oneTickLater = ReviewRules.Evaluate(Ctx(), ClosedAt.AddDays(14).AddTicks(1));

        Assert.True(onTheLastMoment.CanWrite);
        Assert.False(oneTickLater.CanWrite);
        Assert.Equal(ReviewUnavailable.WindowClosed, oneTickLater.Reason);
        Assert.Equal(ClosedAt.AddDays(14), oneTickLater.DeadlineAt);
    }

    [Fact]
    public void The_deadline_counts_from_the_closing_of_the_exchange_not_from_its_start()
    {
        var e = ReviewRules.Evaluate(Ctx(closedAt: ClosedAt.AddDays(100)), ClosedAt.AddDays(105));

        Assert.True(e.CanWrite);
        Assert.Equal(ClosedAt.AddDays(114), e.DeadlineAt);
    }

    [Fact]
    public void An_expired_window_keeps_the_one_sided_flag_for_the_screen()
    {
        var e = ReviewRules.Evaluate(Ctx(completed: 0, mineTaught: 0, partnerNoShows: 3), ClosedAt.AddDays(20));

        Assert.False(e.CanWrite);
        Assert.Equal(ReviewUnavailable.WindowClosed, e.Reason);
        Assert.True(e.OneSided);
    }

    // ---------- «Полный обмен» ----------

    [Theory]
    [InlineData(ExchangeStatus.Completed, 3, 3, true)]
    [InlineData(ExchangeStatus.Completed, 5, 4, true)]
    [InlineData(ExchangeStatus.Completed, 3, 2, false)]
    [InlineData(ExchangeStatus.Completed, 2, 3, false)]
    [InlineData(ExchangeStatus.Completed, 0, 3, false)]
    [InlineData(ExchangeStatus.Aborted, 3, 3, false)]   // прерванный обмен «полным» не бывает
    public void The_full_exchange_mark_needs_a_normal_end_and_at_least_three_lessons_taught_by_each(ExchangeStatus status, int mine, int partner, bool full)
    {
        var e = ReviewRules.Evaluate(Ctx(status, completed: mine + partner, mineTaught: mine, partnerTaught: partner), Now);

        Assert.True(e.CanWrite);
        Assert.Equal(full, e.IsFullExchange);
    }

    [Fact]
    public void Constants_match_the_specification()
    {
        Assert.Equal(3, ReviewRules.NoShowThreshold);
        Assert.Equal(3, ReviewRules.FullExchangeLessons);
        Assert.Equal(TimeSpan.FromDays(14), Review.WriteWindow);
    }
}
