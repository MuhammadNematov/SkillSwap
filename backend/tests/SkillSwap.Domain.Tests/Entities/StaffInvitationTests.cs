using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Приглашение служебного аккаунта (раздел 3 ТЗ): срок 72 часа, состояния, повторная отправка, отзыв.</summary>
public class StaffInvitationTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string Hash = new('a', 64);
    private static readonly string Hash2 = new('b', 64);
    private static readonly Guid Inviter = Guid.NewGuid();

    private static StaffInvitation New(UserRole role = UserRole.Moderator, string email = "new.moderator@example.com") =>
        StaffInvitation.Create(email, role, Inviter, Hash, Now);

    [Fact]
    public void A_new_invitation_is_pending_for_72_hours_with_a_normalized_address()
    {
        var invitation = New(email: "  New.Moderator@Example.COM ");

        Assert.Equal("new.moderator@example.com", invitation.Email);
        Assert.Equal(Now.AddHours(72), invitation.ExpiresAt);
        Assert.Equal(StaffInvitationStatus.Pending, invitation.StatusAt(Now));
        Assert.True(invitation.IsPending(Now.AddHours(72)));                 // последний момент включён
        Assert.Equal(StaffInvitationStatus.Expired, invitation.StatusAt(Now.AddHours(72).AddTicks(1)));
        Assert.Equal(TimeSpan.FromHours(72), StaffInvitation.Lifetime);
    }

    [Theory]
    [InlineData(UserRole.Moderator)]
    [InlineData(UserRole.Admin)]
    public void Moderators_and_admins_can_be_invited(UserRole role)
    {
        Assert.Equal(role, New(role).TargetRole);
    }

    [Theory]
    [InlineData(UserRole.User)]
    [InlineData(UserRole.SuperAdmin)]   // суперадмин создаётся при развёртывании, не по приглашению
    public void Other_roles_cannot_be_invited(UserRole role)
    {
        var error = Assert.Throws<DomainValidationException>(() => New(role));

        Assert.Equal("invitation_role_invalid", error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("@example.com")]
    [InlineData("name@")]
    public void An_invalid_address_is_rejected(string? email)
    {
        var error = Assert.Throws<DomainValidationException>(() => New(email: email!));

        Assert.Equal("invitation_email_invalid", error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void The_token_hash_must_be_a_sha256_hex(string hash)
    {
        var error = Assert.Throws<DomainValidationException>(() => StaffInvitation.Create("a@example.com", UserRole.Moderator, Inviter, hash, Now));

        Assert.Equal("invitation_token_invalid", error.Code);
    }

    [Fact]
    public void Accepting_a_pending_invitation_records_the_new_account()
    {
        var invitation = New();
        var user = Guid.NewGuid();

        invitation.Accept(user, Now.AddHours(1));

        Assert.Equal(StaffInvitationStatus.Accepted, invitation.StatusAt(Now.AddDays(30)));   // принятое остаётся принятым и после срока
        Assert.Equal(user, invitation.AcceptedUserId);
        Assert.Equal(Now.AddHours(1), invitation.AcceptedAt);
    }

    [Fact]
    public void An_expired_revoked_or_used_invitation_cannot_be_accepted()
    {
        var expired = New();
        var revoked = New();
        revoked.Revoke(Now);
        var used = New();
        used.Accept(Guid.NewGuid(), Now);

        Assert.Equal("invitation_expired", Assert.Throws<DomainValidationException>(() => expired.Accept(Guid.NewGuid(), Now.AddHours(73))).Code);
        Assert.Equal("invitation_revoked", Assert.Throws<DomainValidationException>(() => revoked.Accept(Guid.NewGuid(), Now.AddHours(1))).Code);
        Assert.Equal("invitation_already_used", Assert.Throws<DomainValidationException>(() => used.Accept(Guid.NewGuid(), Now.AddHours(1))).Code);
    }

    [Fact]
    public void Revoking_works_once_and_never_on_an_accepted_invitation()
    {
        var invitation = New();

        Assert.True(invitation.Revoke(Now.AddHours(1)));
        Assert.False(invitation.Revoke(Now.AddHours(2)));   // повтор безвреден
        Assert.Equal(StaffInvitationStatus.Revoked, invitation.StatusAt(Now.AddHours(2)));

        var accepted = New();
        accepted.Accept(Guid.NewGuid(), Now);
        Assert.Throws<InvalidStateTransitionException>(() => accepted.Revoke(Now.AddHours(1)));

        var expired = New();
        Assert.False(expired.Revoke(Now.AddHours(100)));   // истёкшее отзывать нечего
        Assert.Equal(StaffInvitationStatus.Expired, expired.StatusAt(Now.AddHours(100)));
    }

    [Fact]
    public void Renewing_gives_a_new_token_and_a_new_term_and_kills_the_old_link()
    {
        var invitation = New();

        invitation.Renew(Hash2, Now.AddHours(100));   // истёкшее тоже можно отправить заново

        Assert.Equal(Hash2, invitation.TokenHash);
        Assert.Equal(Now.AddHours(172), invitation.ExpiresAt);
        Assert.Equal(StaffInvitationStatus.Pending, invitation.StatusAt(Now.AddHours(101)));
        Assert.Equal(1, invitation.ResendCount);
    }

    [Fact]
    public void An_accepted_or_revoked_invitation_cannot_be_resent()
    {
        var accepted = New();
        accepted.Accept(Guid.NewGuid(), Now);
        var revoked = New();
        revoked.Revoke(Now);

        Assert.Throws<InvalidStateTransitionException>(() => accepted.Renew(Hash2, Now.AddHours(1)));
        Assert.Throws<InvalidStateTransitionException>(() => revoked.Renew(Hash2, Now.AddHours(1)));
    }
}
