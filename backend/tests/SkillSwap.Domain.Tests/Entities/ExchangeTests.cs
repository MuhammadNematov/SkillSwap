using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Статусная модель обмена (раздел 9.2 ТЗ).</summary>
public class ExchangeTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static Exchange New(int days = 30) => Exchange.Create(Guid.NewGuid(), A, B, Guid.NewGuid(), Guid.NewGuid(), days, Now);

    [Fact]
    public void A_new_exchange_is_active_and_lasts_the_requested_days()
    {
        var e = New(60);

        Assert.Equal(ExchangeStatus.Active, e.Status);
        Assert.Equal(Now, e.StartDate);
        Assert.Equal(Now.AddDays(60), e.EndDate);
        Assert.False(e.IsExtended);
        Assert.Equal(B, e.PartnerOf(A));
        Assert.Equal(A, e.PartnerOf(B));
    }

    [Fact]
    public void An_exchange_needs_two_different_people_and_a_positive_term()
    {
        Assert.Throws<DomainValidationException>(() => Exchange.Create(Guid.NewGuid(), A, A, Guid.NewGuid(), Guid.NewGuid(), 30, Now));
        Assert.Throws<DomainValidationException>(() => New(0));
    }

    [Fact]
    public void Completes_only_when_both_pressed_and_repeats_are_harmless()
    {
        var e = New();

        Assert.False(e.MarkCompleted(A, Now));
        Assert.False(e.MarkCompleted(A, Now));
        Assert.Equal(ExchangeStatus.Active, e.Status);
        Assert.True(e.MarkCompleted(B, Now.AddDays(1)));

        Assert.Equal(ExchangeStatus.Completed, e.Status);
        Assert.Equal(Now.AddDays(1), e.ClosedAt);
        Assert.Throws<InvalidStateTransitionException>(() => e.MarkCompleted(A, Now));
    }

    [Fact]
    public void A_stranger_cannot_act_on_the_exchange()
    {
        var e = New();
        var stranger = Guid.NewGuid();

        Assert.Equal("not_participant", Assert.Throws<DomainValidationException>(() => e.MarkCompleted(stranger, Now)).Code);
        Assert.Throws<DomainValidationException>(() => e.Abort(stranger, "x", Now));
        Assert.Throws<DomainValidationException>(() => e.AcceptExtension(stranger));
    }

    [Fact]
    public void Completes_by_time_only_when_active_and_due()
    {
        var e = New();

        Assert.False(e.CompleteIfDue(Now.AddDays(29)));
        Assert.True(e.CompleteIfDue(Now.AddDays(30)));
        Assert.Equal(ExchangeStatus.Completed, e.Status);
        Assert.False(e.CompleteIfDue(Now.AddDays(31)));   // повтор ничего не меняет

        var frozen = New();
        frozen.Freeze(Now.AddDays(1));
        Assert.False(frozen.CompleteIfDue(Now.AddDays(40)));   // замороженный по сроку не завершается
        Assert.Equal(ExchangeStatus.Frozen, frozen.Status);
    }

    [Fact]
    public void Abort_needs_a_reason_and_records_who_left()
    {
        var e = New();

        Assert.Equal("abort_reason_required", Assert.Throws<DomainValidationException>(() => e.Abort(A, "  ", Now)).Code);
        Assert.Equal("abort_reason_too_long", Assert.Throws<DomainValidationException>(() => e.Abort(A, new string('a', 501), Now)).Code);
        Assert.Equal(ExchangeStatus.Active, e.Status);

        e.Abort(B, " Переезд ", Now.AddDays(2));

        Assert.Equal(ExchangeStatus.Aborted, e.Status);
        Assert.Equal(B, e.AbortedById);
        Assert.Equal("Переезд", e.AbortReason);
        Assert.Equal(Now.AddDays(2), e.ClosedAt);
    }

    [Fact]
    public void A_participant_cannot_abort_a_frozen_or_finished_exchange()
    {
        var frozen = New();
        frozen.Freeze(Now);
        Assert.Throws<InvalidStateTransitionException>(() => frozen.Abort(A, "x", Now));

        var done = New();
        done.Abort(A, "x", Now);
        Assert.Throws<InvalidStateTransitionException>(() => done.Abort(B, "y", Now));
    }

    [Fact]
    public void A_block_aborts_without_a_culprit_and_only_from_active()
    {
        var e = New();

        e.AbortByBlock(A, Now);

        Assert.Equal(ExchangeStatus.Aborted, e.Status);
        Assert.Null(e.AbortedById);
        Assert.Equal(Exchange.AbortReasonBlocked, e.AbortReason);
        Assert.Equal(A, e.BlockedById);
        Assert.Throws<InvalidStateTransitionException>(() => e.AbortByBlock(A, Now));
    }

    [Fact]
    public void A_moderator_can_close_active_and_frozen_exchanges()
    {
        var active = New();
        active.AbortByModerator("нарушение", Now);
        Assert.Equal(ExchangeStatus.Aborted, active.Status);
        Assert.Null(active.AbortedById);

        var frozen = New();
        frozen.Freeze(Now);
        frozen.AbortByModerator("по итогам проверки", Now);
        Assert.Equal(ExchangeStatus.Aborted, frozen.Status);
    }

    [Fact]
    public void Unfreezing_shifts_the_end_date_by_the_time_frozen()
    {
        var e = New();
        var originalEnd = e.EndDate;

        e.Freeze(Now.AddDays(5));
        Assert.Equal(ExchangeStatus.Frozen, e.Status);
        Assert.Equal(Now.AddDays(5), e.FrozenAt);

        e.Unfreeze(Now.AddDays(12));

        Assert.Equal(ExchangeStatus.Active, e.Status);
        Assert.Null(e.FrozenAt);
        Assert.Equal(originalEnd.AddDays(7), e.EndDate);
        Assert.Throws<InvalidStateTransitionException>(() => e.Unfreeze(Now));
        Assert.Throws<InvalidStateTransitionException>(() => New().Unfreeze(Now));
    }

    [Fact]
    public void The_extension_applies_when_both_agree_and_only_once()
    {
        var e = New();
        var end = e.EndDate;

        Assert.False(e.AcceptExtension(A));
        Assert.False(e.AcceptExtension(A));   // повтор не засчитывается за согласие партнёра
        Assert.Equal(end, e.EndDate);
        Assert.True(e.AcceptExtension(B));

        Assert.True(e.IsExtended);
        Assert.Equal(end.AddDays(30), e.EndDate);
        Assert.False(e.ExtensionAcceptedByA);
        Assert.Equal(ExchangeStatus.Active, e.Status);   // продление статус не меняет
        Assert.Equal("already_extended", Assert.Throws<DomainValidationException>(() => e.AcceptExtension(A)).Code);
    }

    [Fact]
    public void Only_an_active_exchange_can_be_extended()
    {
        var frozen = New();
        frozen.Freeze(Now);
        Assert.Throws<InvalidStateTransitionException>(() => frozen.AcceptExtension(A));

        var done = New();
        done.AbortByBlock(A, Now);
        Assert.Throws<InvalidStateTransitionException>(() => done.AcceptExtension(A));
    }
}
