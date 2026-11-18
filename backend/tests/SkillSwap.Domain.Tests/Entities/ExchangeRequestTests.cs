using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Статусная модель заявки (раздел 9.1 ТЗ): допустимы только переходы из Pending.</summary>
public class ExchangeRequestTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid From = Guid.NewGuid();
    private static readonly Guid To = Guid.NewGuid();
    private static readonly Guid Mine = Guid.NewGuid();
    private static readonly Guid Theirs = Guid.NewGuid();

    private static ExchangeRequest New(Guid? fromSkill = null, int days = 30, string? message = null) =>
        ExchangeRequest.Create(From, To, fromSkill, Theirs, days, message, Now);

    [Fact]
    public void A_new_request_is_pending_and_expires_in_seven_days()
    {
        var request = New(Mine, 60, "  Привет  ");

        Assert.Equal(ExchangeRequestStatus.Pending, request.Status);
        Assert.Equal(Now.AddDays(7), request.ExpiresAt);
        Assert.Equal("Привет", request.Message);
        Assert.Null(request.RespondedAt);
        Assert.True(request.IsOpenAt(Now));
        Assert.False(request.IsOpenAt(Now.AddDays(7)));   // в момент истечения уже не открыта
    }

    [Fact]
    public void A_blank_message_is_stored_as_null() => Assert.Null(New(message: "   ").Message);

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(45)]
    [InlineData(90)]
    public void Only_30_and_60_days_are_allowed(int days)
    {
        var ex = Assert.Throws<DomainValidationException>(() => New(days: days));
        Assert.Equal("invalid_duration", ex.Code);
    }

    [Fact]
    public void A_request_to_oneself_is_refused()
    {
        var ex = Assert.Throws<DomainValidationException>(() => ExchangeRequest.Create(From, From, null, Theirs, 30, null, Now));
        Assert.Equal("cannot_request_self", ex.Code);
    }

    [Fact]
    public void A_message_over_the_limit_is_refused()
    {
        Assert.Equal(1000, ExchangeRequest.MessageMaxLength);
        Assert.NotNull(New(message: new string('a', 1000)));
        Assert.Throws<DomainValidationException>(() => New(message: new string('a', 1001)));
    }

    [Fact]
    public void Accept_keeps_the_senders_skill_and_records_the_time()
    {
        var request = New(Mine);

        request.Accept(null, Now.AddHours(1));

        Assert.Equal(ExchangeRequestStatus.Accepted, request.Status);
        Assert.Equal(Mine, request.FromTeachesSkillId);
        Assert.Equal(Now.AddHours(1), request.RespondedAt);
    }

    [Fact]
    public void Accept_without_the_senders_skill_needs_a_choice_and_stores_it()
    {
        var request = New();

        var ex = Assert.Throws<DomainValidationException>(() => request.Accept(null, Now));
        Assert.Equal("from_skill_required", ex.Code);
        Assert.Equal(ExchangeRequestStatus.Pending, request.Status);   // неудача статус не меняет

        request.Accept(Mine, Now);

        Assert.Equal(Mine, request.FromTeachesSkillId);
    }

    [Fact]
    public void Accept_cannot_replace_the_senders_skill_but_may_repeat_it()
    {
        var request = New(Mine);

        var ex = Assert.Throws<DomainValidationException>(() => request.Accept(Guid.NewGuid(), Now));
        Assert.Equal("from_skill_fixed", ex.Code);

        request.Accept(Mine, Now);
        Assert.Equal(ExchangeRequestStatus.Accepted, request.Status);
    }

    [Theory]
    [InlineData(ExchangeRequestStatus.Declined)]
    [InlineData(ExchangeRequestStatus.Withdrawn)]
    [InlineData(ExchangeRequestStatus.Expired)]
    public void Pending_closes_into_declined_withdrawn_or_expired(ExchangeRequestStatus target)
    {
        var request = New(Mine);

        switch (target)
        {
            case ExchangeRequestStatus.Declined: request.Decline(Now); break;
            case ExchangeRequestStatus.Withdrawn: request.Withdraw(Now); break;
            default: request.Expire(Now); break;
        }

        Assert.Equal(target, request.Status);
        Assert.Equal(Now, request.RespondedAt);
    }

    public static IEnumerable<object[]> Finished()
    {
        foreach (var close in new[] { "accept", "decline", "withdraw", "expire" })
        {
            yield return [close];
        }
    }

    private static void Apply(ExchangeRequest request, string action)
    {
        switch (action)
        {
            case "accept": request.Accept(Mine, Now); break;
            case "decline": request.Decline(Now); break;
            case "withdraw": request.Withdraw(Now); break;
            default: request.Expire(Now); break;
        }
    }

    [Theory]
    [MemberData(nameof(Finished))]
    public void No_transition_leaves_a_finished_request(string firstAction)
    {
        foreach (var second in new[] { "accept", "decline", "withdraw", "expire" })
        {
            var request = New(Mine);
            Apply(request, firstAction);
            var before = request.Status;

            var ex = Assert.Throws<InvalidStateTransitionException>(() => Apply(request, second));

            Assert.Equal(InvalidStateTransitionException.ErrorCode, ex.Code);
            Assert.Equal(before, request.Status);
        }
    }
}
