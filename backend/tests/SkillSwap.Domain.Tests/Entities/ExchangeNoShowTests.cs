using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Выход пострадавшего после трёх окончательных неявок партнёра (раздел 6.3 ТЗ): виновным записывается партнёр, признак остаётся.</summary>
public class ExchangeNoShowTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static Exchange New() => Exchange.Create(Guid.NewGuid(), A, B, Guid.NewGuid(), Guid.NewGuid(), 30, Now);

    [Fact]
    public void After_three_no_shows_the_victim_leaves_and_the_partner_is_recorded_as_the_one_who_left()
    {
        var e = New();

        e.AbortForPartnerNoShow(A, 3, Now.AddDays(5));

        Assert.Equal(ExchangeStatus.Aborted, e.Status);
        Assert.Equal(B, e.AbortedById);                       // виновным записан партнёр, а не вышедший
        Assert.Equal(Exchange.AbortReasonPartnerNoShow, e.AbortReason);
        Assert.True(e.AbortedForNoShow);
        Assert.Equal(Now.AddDays(5), e.ClosedAt);
    }

    [Fact]
    public void The_same_works_for_the_other_participant()
    {
        var e = New();

        e.AbortForPartnerNoShow(B, 4, Now);

        Assert.Equal(A, e.AbortedById);
        Assert.True(e.AbortedForNoShow);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Fewer_than_three_no_shows_is_not_enough(int count)
    {
        var e = New();

        var error = Assert.Throws<DomainValidationException>(() => e.AbortForPartnerNoShow(A, count, Now));

        Assert.Equal("no_show_threshold_not_reached", error.Code);
        Assert.Equal(ExchangeStatus.Active, e.Status);
        Assert.False(e.AbortedForNoShow);
    }

    [Fact]
    public void Only_participants_and_only_an_active_exchange_can_do_it()
    {
        Assert.Throws<DomainValidationException>(() => New().AbortForPartnerNoShow(Guid.NewGuid(), 3, Now));

        var closed = New();
        closed.Abort(A, "причина", Now);
        Assert.Throws<InvalidStateTransitionException>(() => closed.AbortForPartnerNoShow(A, 3, Now));

        var frozen = New();
        frozen.Freeze(Now);
        Assert.Throws<InvalidStateTransitionException>(() => frozen.AbortForPartnerNoShow(A, 3, Now));
    }

    [Fact]
    public void An_ordinary_exit_never_sets_the_no_show_mark_even_with_a_similar_reason_text()
    {
        var e = New();

        e.Abort(A, "partner_no_show", Now);

        Assert.False(e.AbortedForNoShow);
        Assert.Equal(A, e.AbortedById);   // обычный выход: виновный — вышедший
    }
}
