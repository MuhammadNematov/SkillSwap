using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Доступ к чату, предупреждения, запросы на бан, апелляция, вопросы сторонам, аудит и привязка личного аккаунта.</summary>
public class ModerationSupportTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Case1 = Guid.NewGuid();
    private static readonly Guid Moderator = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Reviewer = Guid.NewGuid();
    private static readonly Guid User1 = Guid.NewGuid();
    private static readonly string Reason = "Подробное обоснование не короче двадцати символов.";

    // ---------- доступ к чату ----------

    [Fact]
    public void A_chat_access_grant_is_active_until_revoked_and_is_revoked_once()
    {
        var grant = ChatAccessGrant.Create(Case1, Moderator, Guid.NewGuid(), GrantReason.Case, Now);

        Assert.True(grant.IsActive);
        Assert.Equal(Now, grant.GrantedAt);
        Assert.Null(grant.RevokedAt);

        Assert.True(grant.Revoke(Now.AddHours(5)));
        Assert.False(grant.IsActive);
        Assert.Equal(Now.AddHours(5), grant.RevokedAt);

        Assert.False(grant.Revoke(Now.AddDays(1)));   // повтор безвреден, момент первого отзыва остаётся
        Assert.Equal(Now.AddHours(5), grant.RevokedAt);
    }

    [Fact]
    public void A_grant_remembers_why_it_was_given()
    {
        Assert.Equal(GrantReason.AppealReview, ChatAccessGrant.Create(Case1, Reviewer, Guid.NewGuid(), GrantReason.AppealReview, Now).Reason);
    }

    // ---------- предупреждения ----------

    [Fact]
    public void A_warning_lasts_twelve_months_and_is_active_until_its_last_moment()
    {
        var w = Warning.Issue(User1, Case1, Moderator, Reason, Now);

        Assert.Equal(Now.AddMonths(12), w.ExpiresAt);
        Assert.True(w.IsActiveAt(Now));
        Assert.True(w.IsActiveAt(Now.AddMonths(12).AddTicks(-1)));
        Assert.False(w.IsActiveAt(Now.AddMonths(12)));
    }

    [Fact]
    public void A_revoked_warning_is_no_longer_active_and_is_revoked_once()
    {
        var w = Warning.Issue(User1, Case1, Moderator, Reason, Now);

        Assert.True(w.Revoke(Now.AddDays(10)));
        Assert.False(w.IsActiveAt(Now.AddDays(11)));
        Assert.False(w.Revoke(Now.AddDays(20)));
        Assert.Equal(Now.AddDays(10), w.RevokedAt);
    }

    [Fact]
    public void A_warning_needs_a_real_reason_and_three_active_ones_are_the_ban_threshold()
    {
        Assert.Equal("warning_reason_too_short", Assert.Throws<DomainValidationException>(() => Warning.Issue(User1, Case1, Moderator, "мало", Now)).Code);
        Assert.Equal(3, Warning.BanThreshold);
        Assert.Equal(12, Warning.ValidMonths);
    }

    // ---------- запросы на бан ----------

    [Fact]
    public void A_ban_request_from_a_moderator_carries_a_reason_and_waits_for_the_admin()
    {
        var b = BanRequest.ByModerator(User1, Case1, Moderator, Reason);

        Assert.Equal(BanRequestStatus.Pending, b.Status);
        Assert.Equal(Moderator, b.RequestedById);
        Assert.Equal(Case1, b.CaseId);
        Assert.Equal("ban_reason_too_short", Assert.Throws<DomainValidationException>(() => BanRequest.ByModerator(User1, Case1, Moderator, "мало")).Code);
    }

    [Fact]
    public void An_automatic_ban_request_has_no_author()
    {
        var b = BanRequest.Automatic(User1, null, "Три действующих предупреждения за 12 месяцев.");

        Assert.Null(b.RequestedById);
        Assert.Equal(BanRequestStatus.Pending, b.Status);
    }

    [Fact]
    public void The_admin_decides_a_ban_request_once()
    {
        var approved = BanRequest.ByModerator(User1, Case1, Moderator, Reason);
        approved.Approve(Admin, Now);
        Assert.Equal(BanRequestStatus.Approved, approved.Status);
        Assert.Equal(Admin, approved.DecidedById);
        Assert.Equal(Now, approved.DecidedAt);
        Assert.Throws<InvalidStateTransitionException>(() => approved.Reject(Admin, Now));
        Assert.Throws<InvalidStateTransitionException>(() => approved.Approve(Admin, Now));

        var rejected = BanRequest.ByModerator(User1, Case1, Moderator, Reason);
        rejected.Reject(Admin, Now);
        Assert.Equal(BanRequestStatus.Rejected, rejected.Status);
        Assert.Throws<InvalidStateTransitionException>(() => rejected.Approve(Admin, Now));
    }

    // ---------- апелляция ----------

    [Fact]
    public void An_appeal_needs_a_reason()
    {
        Assert.Equal("appeal_reason_too_short", Assert.Throws<DomainValidationException>(() => CaseAppeal.Create(Case1, User1, "несогласен")).Code);
        Assert.Equal(User1, CaseAppeal.Create(Case1, User1, Reason).AppellantId);
    }

    [Fact]
    public void The_admin_can_decide_at_once_without_a_re_review()
    {
        var a = CaseAppeal.Create(Case1, User1, Reason);

        a.Decide(Admin, AppealOutcome.Upheld, null, Now);

        Assert.Equal(AppealOutcome.Upheld, a.Outcome);
        Assert.Equal(Admin, a.AdminId);
        Assert.Equal(Now, a.DecidedAt);
    }

    [Fact]
    public void A_re_review_needs_the_conclusion_of_the_assigned_moderator_before_the_final_decision()
    {
        var a = CaseAppeal.Create(Case1, User1, Reason);
        a.AssignReviewer(Admin, Reviewer);

        Assert.Equal("appeal_conclusion_required", Assert.Throws<DomainValidationException>(() => a.Decide(Admin, AppealOutcome.Upheld, null, Now)).Code);
        Assert.Equal("not_appeal_reviewer", Assert.Throws<DomainValidationException>(() => a.WriteConclusion(Moderator, Reason)).Code);
        Assert.Equal("appeal_conclusion_too_short", Assert.Throws<DomainValidationException>(() => a.WriteConclusion(Reviewer, "мало")).Code);

        a.WriteConclusion(Reviewer, Reason);
        Assert.Equal(Reason, a.ReviewerConclusion);
        Assert.Equal("appeal_conclusion_already_written", Assert.Throws<DomainValidationException>(() => a.WriteConclusion(Reviewer, Reason)).Code);

        a.Decide(Admin, AppealOutcome.Overturned, null, Now);
        Assert.Equal(AppealOutcome.Overturned, a.Outcome);
    }

    [Fact]
    public void The_reviewer_is_assigned_once_and_not_after_the_decision()
    {
        var a = CaseAppeal.Create(Case1, User1, Reason);
        a.AssignReviewer(Admin, Reviewer);
        Assert.Equal("appeal_reviewer_already_assigned", Assert.Throws<DomainValidationException>(() => a.AssignReviewer(Admin, Guid.NewGuid())).Code);

        var decided = CaseAppeal.Create(Case1, User1, Reason);
        decided.Decide(Admin, AppealOutcome.Upheld, null, Now);
        Assert.Throws<InvalidStateTransitionException>(() => decided.AssignReviewer(Admin, Reviewer));
        Assert.Throws<InvalidStateTransitionException>(() => decided.Decide(Admin, AppealOutcome.Overturned, null, Now));
    }

    [Fact]
    public void A_replacement_decision_is_named_exactly_when_the_outcome_is_changed()
    {
        var needsDecision = CaseAppeal.Create(Case1, User1, Reason);
        Assert.Equal("appeal_final_decision_mismatch", Assert.Throws<DomainValidationException>(() => needsDecision.Decide(Admin, AppealOutcome.Changed, null, Now)).Code);

        var extra = CaseAppeal.Create(Case1, User1, Reason);
        Assert.Equal("appeal_final_decision_mismatch", Assert.Throws<DomainValidationException>(() => extra.Decide(Admin, AppealOutcome.Upheld, CaseDecision.Warning, Now)).Code);

        var changed = CaseAppeal.Create(Case1, User1, Reason);
        changed.Decide(Admin, AppealOutcome.Changed, CaseDecision.Warning, Now);
        Assert.Equal(CaseDecision.Warning, changed.FinalDecision);
    }

    // ---------- вопросы сторонам ----------

    [Fact]
    public void A_question_and_its_answer_are_linked_and_must_not_be_empty_or_addressed_to_oneself()
    {
        var question = CaseMessage.Create(Case1, Moderator, User1, "  Почему вы не пришли на урок?  ");
        var answer = CaseMessage.Create(Case1, User1, Moderator, "Заболел, предупредил в чате.", question.Id);

        Assert.Equal("Почему вы не пришли на урок?", question.Text);
        Assert.Null(question.InReplyToId);
        Assert.Equal(question.Id, answer.InReplyToId);
        Assert.Equal("case_message_empty", Assert.Throws<DomainValidationException>(() => CaseMessage.Create(Case1, Moderator, User1, "  ")).Code);
        Assert.Equal("case_message_too_long", Assert.Throws<DomainValidationException>(() => CaseMessage.Create(Case1, Moderator, User1, new string('я', 2001))).Code);
        Assert.Equal("case_message_self", Assert.Throws<DomainValidationException>(() => CaseMessage.Create(Case1, Moderator, Moderator, "сам себе")).Code);
    }

    // ---------- доказательства ----------

    [Fact]
    public void Evidence_links_a_case_to_a_message()
    {
        var messageId = Guid.NewGuid();

        var e = CaseEvidence.Create(Case1, messageId, Now);

        Assert.Equal(Case1, e.CaseId);
        Assert.Equal(messageId, e.MessageId);
        Assert.Equal(Now, e.AddedAt);
        Assert.Equal(20, CaseRules.MaxEvidenceMessages);
    }

    // ---------- журнал аудита ----------

    [Fact]
    public void An_audit_entry_keeps_who_did_what_to_which_object_and_from_where()
    {
        var target = Guid.NewGuid();

        var log = AuditLog.Create(Moderator, "ChatRead", "Exchange", target, "{\"caseId\":\"x\"}", "10.1.2.3", Now);

        Assert.Equal(Moderator, log.ActorId);
        Assert.Equal("ChatRead", log.Action);
        Assert.Equal("Exchange", log.TargetType);
        Assert.Equal(target, log.TargetId);
        Assert.Equal("{\"caseId\":\"x\"}", log.DetailsJson);
        Assert.Equal("10.1.2.3", log.IpAddress);
        Assert.Equal(Now, log.CreatedAt);
    }

    [Fact]
    public void An_audit_entry_defaults_empty_details_and_trims_a_long_address_and_refuses_empty_names()
    {
        var log = AuditLog.Create(null, "WarningIssued", "User", null, "  ", new string('1', 200), Now);

        Assert.Null(log.ActorId);
        Assert.Equal("{}", log.DetailsJson);
        Assert.Equal(AuditLog.IpMaxLength, log.IpAddress!.Length);
        Assert.Equal("audit_action_invalid", Assert.Throws<DomainValidationException>(() => AuditLog.Create(null, " ", "User", null, null, null, Now)).Code);
        Assert.Equal("audit_action_invalid", Assert.Throws<DomainValidationException>(() => AuditLog.Create(null, new string('a', 65), "User", null, null, null, Now)).Code);
        Assert.Equal("audit_target_invalid", Assert.Throws<DomainValidationException>(() => AuditLog.Create(null, "X", "", null, null, null, Now)).Code);
    }

    // ---------- привязка личного аккаунта ----------

    [Fact]
    public void A_staff_account_links_to_a_personal_one_and_nothing_else_does()
    {
        var staff = User.CreateStaff("mod@skillswap.test", "Модератор", UserRole.Moderator);
        var personal = User.CreateRegular("ali@example.com", "Али", new DateOnly(1990, 1, 1), "ru");

        staff.LinkPersonalAccount(personal);

        Assert.Equal(personal.Id, staff.LinkedUserId);
        Assert.Equal("linked_account_staff_only", Assert.Throws<DomainValidationException>(() => personal.LinkPersonalAccount(personal)).Code);
        Assert.Equal("linked_account_must_be_regular", Assert.Throws<DomainValidationException>(() =>
            staff.LinkPersonalAccount(User.CreateStaff("other@skillswap.test", "Другой", UserRole.Admin))).Code);
    }
}
