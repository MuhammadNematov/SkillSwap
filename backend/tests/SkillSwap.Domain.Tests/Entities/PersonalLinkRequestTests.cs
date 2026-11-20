using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Запрос на привязку личного аккаунта (раздел 3 ТЗ, правило 3): срок 24 часа, состояния, подтверждение, отказ, отмена; снятие привязки у пользователя.</summary>
public class PersonalLinkRequestTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string Hash = new('c', 64);
    private static readonly Guid Staff = Guid.NewGuid();
    private static readonly Guid Personal = Guid.NewGuid();

    private static PersonalLinkRequest New() => PersonalLinkRequest.Create(Staff, Personal, Hash, Now);

    [Fact]
    public void A_new_request_is_pending_for_24_hours_and_its_last_moment_is_included()
    {
        var request = New();

        Assert.Equal(Now.AddHours(24), request.ExpiresAt);
        Assert.Equal(PersonalLinkStatus.Pending, request.StatusAt(Now));
        Assert.True(request.IsPending(Now.AddHours(24)));
        Assert.Equal(PersonalLinkStatus.Expired, request.StatusAt(Now.AddHours(24).AddTicks(1)));
        Assert.Equal(TimeSpan.FromHours(24), PersonalLinkRequest.Lifetime);
    }

    [Fact]
    public void The_two_accounts_must_differ_and_the_hash_must_be_a_sha256_hex()
    {
        Assert.Equal("link_same_account", Assert.Throws<DomainValidationException>(() => PersonalLinkRequest.Create(Staff, Staff, Hash, Now)).Code);
        Assert.Equal("link_token_invalid", Assert.Throws<DomainValidationException>(() => PersonalLinkRequest.Create(Staff, Personal, "short", Now)).Code);
    }

    [Fact]
    public void Confirming_a_pending_request_is_final()
    {
        var request = New();

        request.Confirm(Now.AddHours(1));

        Assert.Equal(PersonalLinkStatus.Confirmed, request.StatusAt(Now.AddDays(10)));   // подтверждённое остаётся подтверждённым после срока
        Assert.Equal("link_request_already_confirmed", Assert.Throws<DomainValidationException>(() => request.Confirm(Now.AddHours(2))).Code);
        Assert.Equal("link_request_already_confirmed", Assert.Throws<DomainValidationException>(() => request.Decline(Now.AddHours(2))).Code);
        Assert.False(request.Cancel(Now.AddHours(2)));
    }

    [Fact]
    public void Declining_closes_the_request()
    {
        var request = New();

        request.Decline(Now.AddHours(1));

        Assert.Equal(PersonalLinkStatus.Declined, request.StatusAt(Now.AddHours(2)));
        Assert.Equal("link_request_declined", Assert.Throws<DomainValidationException>(() => request.Confirm(Now.AddHours(2))).Code);
    }

    [Fact]
    public void Cancelling_works_once_and_blocks_later_confirmation()
    {
        var request = New();

        Assert.True(request.Cancel(Now.AddHours(1)));
        Assert.False(request.Cancel(Now.AddHours(2)));   // повтор безвреден
        Assert.Equal(PersonalLinkStatus.Cancelled, request.StatusAt(Now.AddHours(2)));
        Assert.Equal("link_request_cancelled", Assert.Throws<DomainValidationException>(() => request.Confirm(Now.AddHours(2))).Code);
    }

    [Fact]
    public void An_expired_request_cannot_be_confirmed_declined_or_cancelled()
    {
        var request = New();
        var late = Now.AddHours(25);

        Assert.Equal("link_request_expired", Assert.Throws<DomainValidationException>(() => request.Confirm(late)).Code);
        Assert.Equal("link_request_expired", Assert.Throws<DomainValidationException>(() => request.Decline(late)).Code);
        Assert.False(request.Cancel(late));
    }

    [Fact]
    public void A_staff_account_can_drop_its_link_once()
    {
        var staff = User.CreateStaff("moderator@example.com", "Модератор", UserRole.Moderator);
        var personal = User.CreateRegular("person@example.com", "Личный", new DateOnly(1995, 1, 1), "ru");
        staff.LinkPersonalAccount(personal);

        Assert.True(staff.UnlinkPersonalAccount());
        Assert.Null(staff.LinkedUserId);
        Assert.False(staff.UnlinkPersonalAccount());
    }
}
