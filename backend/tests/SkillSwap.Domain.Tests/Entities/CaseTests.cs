using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Обращение (разделы 8.2, 8.3 и 9.4 ТЗ): подача, приоритеты и сроки, все переходы статуса, решения, апелляция.</summary>
public class CaseTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Exchange = Guid.NewGuid();
    private static readonly Guid Opener = Guid.NewGuid();
    private static readonly Guid Against = Guid.NewGuid();
    private static readonly Guid Moderator = Guid.NewGuid();
    private static readonly Guid OtherModerator = Guid.NewGuid();
    private static readonly string Text = "Партнёр обещал уроки и не приходил, пишу подробности ниже.";
    private static readonly string Reason = "Изучил переписку и посещаемость: нарушение подтверждено.";

    private static Case Conduct(bool minor = false, bool fromCall = false) =>
        Case.Open(CaseType.Conduct, CaseCategory.Insults, Exchange, null, Opener, Against, Text, minor, fromCall, Now);

    private static Case Dispute() =>
        Case.Open(CaseType.ReviewDispute, CaseCategory.ReviewUnfair, Exchange, Guid.NewGuid(), Opener, Against, Text, false, false, Now);

    private static Case NoShow() =>
        Case.Open(CaseType.NoShowAbuse, CaseCategory.SystematicEvasion, Exchange, null, Opener, Against, Text, false, false, Now);

    private static Case InProgress(Case? c = null)
    {
        c ??= Conduct();
        c.Take(Moderator);
        return c;
    }

    private static Case Resolved(Case? c = null, CaseDecision decision = CaseDecision.Warning)
    {
        c ??= Conduct();
        c.Take(Moderator);
        c.Resolve(Moderator, decision, false, Reason, Now.AddDays(1));
        return c;
    }

    // ---------- подача ----------

    [Fact]
    public void A_new_case_is_open_and_keeps_who_filed_it_against_whom()
    {
        var c = Conduct();

        Assert.Equal(CaseStatus.Open, c.Status);
        Assert.Equal(Opener, c.OpenedById);
        Assert.Equal(Against, c.AgainstUserId);
        Assert.Null(c.ModeratorId);
        Assert.Equal(Text, c.Description);
        Assert.True(c.IsActive);
        Assert.True(c.IsParty(Opener));
        Assert.True(c.IsParty(Against));
        Assert.False(c.IsParty(Moderator));
        Assert.Equal(Against, c.PartnerOf(Opener));
        Assert.Equal(Opener, c.PartnerOf(Against));
    }

    [Fact]
    public void Priority_and_the_reaction_deadline_follow_the_type()
    {
        var conduct = Conduct();
        Assert.Equal(CasePriority.High, conduct.Priority);                     // жалоба на поведение — высокий
        Assert.Equal(Now.AddHours(24), conduct.DueAt);

        var dispute = Dispute();
        Assert.Equal(CasePriority.Normal, dispute.Priority);                   // спор по отзыву — обычный
        Assert.Equal(Now.AddHours(72), dispute.DueAt);

        var noShow = NoShow();
        Assert.Equal(CasePriority.Normal, noShow.Priority);
        Assert.Equal(Now.AddHours(72), noShow.DueAt);
    }

    [Fact]
    public void A_minor_among_the_participants_makes_any_case_high_priority()
    {
        var dispute = Case.Open(CaseType.ReviewDispute, CaseCategory.ReviewAbusive, Exchange, Guid.NewGuid(), Opener, Against, Text, minorInvolved: true, false, Now);
        var noShow = Case.Open(CaseType.NoShowAbuse, CaseCategory.BadFaithLessons, Exchange, null, Opener, Against, Text, minorInvolved: true, false, Now);

        Assert.All(new[] { dispute, noShow }, c =>
        {
            Assert.Equal(CasePriority.High, c.Priority);
            Assert.Equal(Now.AddHours(24), c.DueAt);
        });
    }

    [Fact]
    public void A_complaint_from_a_call_is_high_priority_and_marked_and_only_conduct_can_come_from_a_call()
    {
        var c = Conduct(fromCall: true);

        Assert.True(c.FromCall);
        Assert.Equal(CasePriority.High, c.Priority);
        Assert.Equal("case_from_call_conduct_only", Assert.Throws<DomainValidationException>(() =>
            Case.Open(CaseType.NoShowAbuse, CaseCategory.SystematicEvasion, Exchange, null, Opener, Against, Text, false, fromCall: true, Now)).Code);
    }

    [Fact]
    public void A_review_dispute_needs_a_review_and_the_other_types_must_not_have_one()
    {
        Assert.Equal("case_review_required", Assert.Throws<DomainValidationException>(() =>
            Case.Open(CaseType.ReviewDispute, CaseCategory.ReviewUnfair, Exchange, null, Opener, Against, Text, false, false, Now)).Code);
        Assert.Equal("case_review_not_allowed", Assert.Throws<DomainValidationException>(() =>
            Case.Open(CaseType.Conduct, CaseCategory.Spam, Exchange, Guid.NewGuid(), Opener, Against, Text, false, false, Now)).Code);
    }

    [Theory]
    [InlineData(CaseType.ReviewDispute, CaseCategory.Insults)]
    [InlineData(CaseType.Conduct, CaseCategory.ReviewFalse)]
    [InlineData(CaseType.Conduct, CaseCategory.SystematicEvasion)]
    [InlineData(CaseType.NoShowAbuse, CaseCategory.Spam)]
    public void The_category_must_belong_to_the_type(CaseType type, CaseCategory category)
    {
        Guid? review = type == CaseType.ReviewDispute ? Guid.NewGuid() : null;

        Assert.Equal("case_category_mismatch", Assert.Throws<DomainValidationException>(() =>
            Case.Open(type, category, Exchange, review, Opener, Against, Text, false, false, Now)).Code);
    }

    [Fact]
    public void Every_type_has_its_own_category_list_and_every_category_belongs_to_exactly_one_type()
    {
        foreach (var category in Enum.GetValues<CaseCategory>())
        {
            Assert.Single(Enum.GetValues<CaseType>(), type => CaseRules.CategoriesFor(type).Contains(category));
        }
    }

    [Fact]
    public void The_description_is_trimmed_and_needs_twenty_to_two_thousand_characters_and_nobody_files_against_themselves()
    {
        Assert.Equal("case_description_too_short", Assert.Throws<DomainValidationException>(() =>
            Case.Open(CaseType.Conduct, CaseCategory.Spam, Exchange, null, Opener, Against, "  коротко  ", false, false, Now)).Code);
        Assert.Equal("case_description_too_short", Assert.Throws<DomainValidationException>(() =>
            Case.Open(CaseType.Conduct, CaseCategory.Spam, Exchange, null, Opener, Against, null, false, false, Now)).Code);
        Assert.Equal("case_description_too_long", Assert.Throws<DomainValidationException>(() =>
            Case.Open(CaseType.Conduct, CaseCategory.Spam, Exchange, null, Opener, Against, new string('я', 2001), false, false, Now)).Code);
        Assert.Equal("case_against_self", Assert.Throws<DomainValidationException>(() =>
            Case.Open(CaseType.Conduct, CaseCategory.Spam, Exchange, null, Opener, Opener, Text, false, false, Now)).Code);

        Assert.Equal(new string('я', 20), Case.Open(CaseType.Conduct, CaseCategory.Spam, Exchange, null, Opener, Against, "  " + new string('я', 20) + " ", false, false, Now).Description);
    }

    [Fact]
    public void An_open_case_is_overdue_only_after_its_reaction_deadline_and_a_taken_one_is_not()
    {
        var c = Conduct();

        Assert.False(c.IsOverdue(c.DueAt));
        Assert.True(c.IsOverdue(c.DueAt.AddTicks(1)));
        c.Take(Moderator);
        Assert.False(c.IsOverdue(c.DueAt.AddDays(5)));
    }

    // ---------- сроки подачи ----------

    [Theory]
    [InlineData(CaseType.ReviewDispute, 14)]
    [InlineData(CaseType.NoShowAbuse, 30)]
    [InlineData(CaseType.Conduct, 90)]
    public void The_filing_window_depends_on_the_type_and_its_last_moment_is_included(CaseType type, int days)
    {
        var anchor = Now;

        Assert.True(CaseRules.IsWithinFilingWindow(type, anchor, anchor.AddDays(days)));
        Assert.False(CaseRules.IsWithinFilingWindow(type, anchor, anchor.AddDays(days).AddTicks(1)));
        Assert.True(CaseRules.IsWithinFilingWindow(type, null, anchor.AddYears(5)));   // обмен ещё идёт: подать можно всегда
    }

    // ---------- отзыв обращения ----------

    [Fact]
    public void Only_the_opener_can_withdraw_and_only_before_a_moderator_takes_the_case()
    {
        var c = Conduct();
        Assert.Equal("not_case_opener", Assert.Throws<DomainValidationException>(() => c.Withdraw(Against)).Code);
        Assert.Equal("not_case_opener", Assert.Throws<DomainValidationException>(() => c.Withdraw(Moderator)).Code);

        c.Withdraw(Opener);

        Assert.Equal(CaseStatus.Withdrawn, c.Status);
        Assert.False(c.IsActive);
        Assert.Throws<InvalidStateTransitionException>(() => c.Take(Moderator));   // отозванное не взять
        Assert.Throws<InvalidStateTransitionException>(() => c.Withdraw(Opener));

        var taken = InProgress();
        Assert.Throws<InvalidStateTransitionException>(() => taken.Withdraw(Opener));   // после взятия отозвать нельзя
    }

    // ---------- взятие ----------

    [Fact]
    public void A_moderator_takes_an_open_case_once()
    {
        var c = Conduct();

        c.Take(Moderator);

        Assert.Equal(CaseStatus.InProgress, c.Status);
        Assert.Equal(Moderator, c.ModeratorId);
        Assert.Throws<InvalidStateTransitionException>(() => c.Take(OtherModerator));   // уже взято
    }

    [Fact]
    public void A_moderator_who_is_a_participant_cannot_take_the_case()
    {
        foreach (var participant in new[] { Opener, Against })
        {
            var c = Conduct();
            Assert.Equal("conflict_of_interest", Assert.Throws<DomainValidationException>(() => c.Take(participant)).Code);
            Assert.Equal(CaseStatus.Open, c.Status);
        }
    }

    // ---------- пояснения ----------

    [Fact]
    public void An_explanation_request_starts_the_forty_eight_hour_timer_and_returns_to_work_once()
    {
        var c = InProgress();

        c.RequestExplanation(Moderator, Now.AddHours(2));

        Assert.Equal(CaseStatus.WaitingResponse, c.Status);
        Assert.Equal(Now.AddHours(50), c.ResponseDeadlineAt);
        Assert.True(c.ResumeAfterExplanation());
        Assert.Equal(CaseStatus.InProgress, c.Status);
        Assert.Null(c.ResponseDeadlineAt);
        Assert.False(c.ResumeAfterExplanation());   // повтор безвреден
    }

    [Fact]
    public void Only_the_assigned_moderator_asks_and_only_from_work()
    {
        var c = InProgress();
        Assert.Equal("not_case_moderator", Assert.Throws<DomainValidationException>(() => c.RequestExplanation(OtherModerator, Now)).Code);

        Assert.Throws<InvalidStateTransitionException>(() => Conduct().RequestExplanation(Moderator, Now));   // не взято

        c.RequestExplanation(Moderator, Now);
        Assert.Throws<InvalidStateTransitionException>(() => c.RequestExplanation(Moderator, Now));          // уже ждём ответ
    }

    [Fact]
    public void Resuming_a_case_that_is_not_waiting_does_nothing()
    {
        Assert.False(Conduct().ResumeAfterExplanation());
        Assert.False(InProgress().ResumeAfterExplanation());
        Assert.False(Resolved().ResumeAfterExplanation());
    }

    // ---------- решение ----------

    [Fact]
    public void A_decision_needs_a_reason_and_sets_the_resolution_time()
    {
        var c = InProgress();
        Assert.Equal("case_reason_too_short", Assert.Throws<DomainValidationException>(() => c.Resolve(Moderator, CaseDecision.Warning, false, "мало", Now)).Code);
        Assert.Equal("case_reason_too_short", Assert.Throws<DomainValidationException>(() => c.Resolve(Moderator, CaseDecision.Warning, false, null, Now)).Code);
        Assert.Equal(CaseStatus.InProgress, c.Status);

        c.Resolve(Moderator, CaseDecision.Warning, false, "  " + Reason + "  ", Now.AddDays(1));

        Assert.Equal(CaseStatus.Resolved, c.Status);
        Assert.Equal(CaseDecision.Warning, c.Decision);
        Assert.Equal(Reason, c.DecisionReason);
        Assert.Equal(Now.AddDays(1), c.ResolvedAt);
    }

    [Fact]
    public void A_decision_can_be_made_from_work_only_by_the_assigned_moderator_and_not_from_waiting()
    {
        var c = InProgress();
        Assert.Equal("not_case_moderator", Assert.Throws<DomainValidationException>(() => c.Resolve(OtherModerator, CaseDecision.Warning, false, Reason, Now)).Code);
        Assert.Throws<InvalidStateTransitionException>(() => Conduct().Resolve(Moderator, CaseDecision.Warning, false, Reason, Now));   // не взято

        c.RequestExplanation(Moderator, Now);
        Assert.Throws<InvalidStateTransitionException>(() => c.Resolve(Moderator, CaseDecision.Warning, false, Reason, Now));
    }

    [Theory]
    [InlineData(CaseType.ReviewDispute, CaseDecision.ReviewKept, true)]
    [InlineData(CaseType.ReviewDispute, CaseDecision.ReviewHidden, true)]
    [InlineData(CaseType.ReviewDispute, CaseDecision.Warning, false)]
    [InlineData(CaseType.ReviewDispute, CaseDecision.ExchangeEnded, false)]
    [InlineData(CaseType.ReviewDispute, CaseDecision.NoViolation, false)]
    [InlineData(CaseType.Conduct, CaseDecision.Warning, true)]
    [InlineData(CaseType.Conduct, CaseDecision.ExchangeEnded, true)]
    [InlineData(CaseType.Conduct, CaseDecision.FrozenBanRequested, true)]
    [InlineData(CaseType.Conduct, CaseDecision.NoViolation, true)]
    [InlineData(CaseType.Conduct, CaseDecision.ReviewKept, false)]
    [InlineData(CaseType.Conduct, CaseDecision.ReviewHidden, false)]
    [InlineData(CaseType.NoShowAbuse, CaseDecision.Warning, true)]
    [InlineData(CaseType.NoShowAbuse, CaseDecision.NoViolation, true)]
    [InlineData(CaseType.NoShowAbuse, CaseDecision.ReviewHidden, false)]
    public void Each_type_allows_its_own_decisions(CaseType type, CaseDecision decision, bool allowed)
    {
        var c = type switch { CaseType.ReviewDispute => Dispute(), CaseType.NoShowAbuse => NoShow(), _ => Conduct() };
        c.Take(Moderator);

        if (allowed)
        {
            c.Resolve(Moderator, decision, false, Reason, Now);
            Assert.Equal(decision, c.Decision);
        }
        else
        {
            Assert.Equal("case_decision_not_allowed", Assert.Throws<DomainValidationException>(() => c.Resolve(Moderator, decision, false, Reason, Now)).Code);
            Assert.Equal(CaseStatus.InProgress, c.Status);
        }
    }

    [Theory]
    [InlineData(CaseDecision.ExchangeEnded, true)]
    [InlineData(CaseDecision.FrozenBanRequested, true)]
    [InlineData(CaseDecision.Warning, false)]       // предупреждение само по себе
    [InlineData(CaseDecision.NoViolation, false)]
    public void A_warning_can_be_added_only_to_decisions_that_allow_it(CaseDecision decision, bool combinable)
    {
        var c = InProgress();

        if (combinable)
        {
            c.Resolve(Moderator, decision, alsoWarned: true, Reason, Now);
            Assert.True(c.AlsoWarned);
        }
        else
        {
            Assert.Equal("case_warning_not_combinable", Assert.Throws<DomainValidationException>(() => c.Resolve(Moderator, decision, true, Reason, Now)).Code);
        }
    }

    [Fact]
    public void Hiding_a_review_can_be_combined_with_a_warning_to_its_author()
    {
        var c = Dispute();
        c.Take(Moderator);

        c.Resolve(Moderator, CaseDecision.ReviewHidden, alsoWarned: true, Reason, Now);

        Assert.True(c.AlsoWarned);
        Assert.Equal(CaseDecision.ReviewHidden, c.Decision);
    }

    // ---------- апелляция ----------

    [Fact]
    public void A_participant_can_appeal_within_seven_days_the_last_moment_included()
    {
        foreach (var who in new[] { Opener, Against })
        {
            var c = Resolved();
            c.Appeal(who, c.ResolvedAt!.Value.AddDays(7));

            Assert.Equal(CaseStatus.Appealed, c.Status);
            Assert.True(c.WasAppealed);
        }
    }

    [Fact]
    public void The_appeal_window_closes_after_seven_days_and_a_stranger_cannot_appeal()
    {
        var late = Resolved();
        Assert.Equal("appeal_window_closed", Assert.Throws<DomainValidationException>(() => late.Appeal(Opener, late.ResolvedAt!.Value.AddDays(7).AddTicks(1))).Code);
        Assert.Equal(CaseStatus.Resolved, late.Status);

        var stranger = Resolved();
        Assert.Equal("not_case_party", Assert.Throws<DomainValidationException>(() => stranger.Appeal(Guid.NewGuid(), stranger.ResolvedAt!.Value.AddDays(1))).Code);
        Assert.Equal("not_case_party", Assert.Throws<DomainValidationException>(() => stranger.Appeal(Moderator, stranger.ResolvedAt!.Value.AddDays(1))).Code);
    }

    [Fact]
    public void The_appeal_happens_once_and_only_after_a_decision()
    {
        var c = Resolved();
        c.Appeal(Opener, Now.AddDays(2));

        Assert.Throws<InvalidStateTransitionException>(() => c.Appeal(Against, Now.AddDays(2)));    // второй раз нельзя
        Assert.Throws<InvalidStateTransitionException>(() => Conduct().Appeal(Opener, Now));        // нет решения
        Assert.Throws<InvalidStateTransitionException>(() => InProgress().Appeal(Opener, Now));
    }

    [Fact]
    public void The_re_review_goes_to_another_moderator_who_is_not_a_participant_and_ends_back_at_the_admin()
    {
        var c = Resolved();
        c.Appeal(Opener, Now.AddDays(2));

        Assert.Equal("appeal_reviewer_must_differ", Assert.Throws<DomainValidationException>(() => c.BeginAppealReview(Moderator)).Code);
        Assert.Equal("conflict_of_interest", Assert.Throws<DomainValidationException>(() => c.BeginAppealReview(Against)).Code);
        Assert.Equal(CaseStatus.Appealed, c.Status);

        c.BeginAppealReview(OtherModerator);
        Assert.Equal(CaseStatus.AppealReview, c.Status);
        c.EndAppealReview();
        Assert.Equal(CaseStatus.Appealed, c.Status);
    }

    [Fact]
    public void The_admin_closes_an_appealed_case_keeping_or_replacing_the_decision()
    {
        var kept = Resolved(decision: CaseDecision.Warning);
        kept.Appeal(Opener, Now.AddDays(2));
        kept.CloseByAdmin(null, false, null, Now.AddDays(3));
        Assert.Equal(CaseStatus.Closed, kept.Status);
        Assert.Equal(CaseDecision.Warning, kept.Decision);
        Assert.Equal(Now.AddDays(3), kept.ClosedAt);
        Assert.False(kept.IsActive);

        var replaced = Resolved(decision: CaseDecision.Warning);
        replaced.Appeal(Opener, Now.AddDays(2));
        replaced.CloseByAdmin(CaseDecision.NoViolation, false, "Повторно изучил материалы: нарушения нет.", Now.AddDays(3));
        Assert.Equal(CaseDecision.NoViolation, replaced.Decision);
        Assert.Equal("Повторно изучил материалы: нарушения нет.", replaced.DecisionReason);
    }

    [Fact]
    public void A_replacement_decision_follows_the_same_rules_as_the_first_one()
    {
        var c = Resolved(decision: CaseDecision.Warning);
        c.Appeal(Opener, Now.AddDays(2));

        Assert.Equal("case_decision_not_allowed", Assert.Throws<DomainValidationException>(() => c.CloseByAdmin(CaseDecision.ReviewHidden, false, Reason, Now)).Code);
        Assert.Equal("case_reason_too_short", Assert.Throws<DomainValidationException>(() => c.CloseByAdmin(CaseDecision.NoViolation, false, "мало", Now)).Code);
        Assert.Equal(CaseStatus.Appealed, c.Status);
        Assert.Equal(CaseDecision.Warning, c.Decision);   // отказ ничего не испортил
    }

    [Fact]
    public void Only_an_appealed_case_can_be_closed_by_the_admin()
    {
        Assert.Throws<InvalidStateTransitionException>(() => Resolved().CloseByAdmin(null, false, null, Now));
        Assert.Throws<InvalidStateTransitionException>(() => InProgress().CloseByAdmin(null, false, null, Now));
    }

    // ---------- закрытие по сроку ----------

    [Fact]
    public void A_resolved_case_closes_after_seven_days_without_an_appeal_and_not_a_moment_earlier()
    {
        var c = Resolved();
        var resolvedAt = c.ResolvedAt!.Value;

        Assert.False(c.CloseIfDue(resolvedAt.AddDays(7).AddTicks(-1)));
        Assert.Equal(CaseStatus.Resolved, c.Status);

        Assert.True(c.CloseIfDue(resolvedAt.AddDays(7)));
        Assert.Equal(CaseStatus.Closed, c.Status);
        Assert.Equal(resolvedAt.AddDays(7), c.ClosedAt);
        Assert.False(c.CloseIfDue(resolvedAt.AddDays(30)));   // повтор ничего не меняет
        Assert.Equal(resolvedAt.AddDays(7), c.ClosedAt);
    }

    [Fact]
    public void An_appealed_or_unresolved_case_is_not_closed_by_the_seven_day_timer()
    {
        var appealed = Resolved();
        appealed.Appeal(Opener, Now.AddDays(2));
        Assert.False(appealed.CloseIfDue(Now.AddYears(1)));
        Assert.Equal(CaseStatus.Appealed, appealed.Status);

        Assert.False(InProgress().CloseIfDue(Now.AddYears(1)));
        Assert.False(Conduct().CloseIfDue(Now.AddYears(1)));
    }

    [Fact]
    public void A_closed_case_accepts_nothing_any_more()
    {
        var c = Resolved();
        c.CloseIfDue(Now.AddDays(30));

        Assert.Throws<InvalidStateTransitionException>(() => c.Appeal(Opener, Now.AddDays(30)));
        Assert.Throws<InvalidStateTransitionException>(() => c.Resolve(Moderator, CaseDecision.Warning, false, Reason, Now));
        Assert.Throws<InvalidStateTransitionException>(() => c.Take(OtherModerator));
        Assert.Throws<InvalidStateTransitionException>(() => c.BeginAppealReview(OtherModerator));
    }

    [Fact]
    public void The_whole_happy_path_walks_through_every_status_of_the_table()
    {
        var c = Conduct();
        Assert.Equal(CaseStatus.Open, c.Status);
        c.Take(Moderator);
        Assert.Equal(CaseStatus.InProgress, c.Status);
        c.RequestExplanation(Moderator, Now);
        Assert.Equal(CaseStatus.WaitingResponse, c.Status);
        c.ResumeAfterExplanation();
        Assert.Equal(CaseStatus.InProgress, c.Status);
        c.Resolve(Moderator, CaseDecision.ExchangeEnded, true, Reason, Now.AddDays(1));
        Assert.Equal(CaseStatus.Resolved, c.Status);
        c.Appeal(Against, Now.AddDays(2));
        Assert.Equal(CaseStatus.Appealed, c.Status);
        c.BeginAppealReview(OtherModerator);
        Assert.Equal(CaseStatus.AppealReview, c.Status);
        c.EndAppealReview();
        Assert.Equal(CaseStatus.Appealed, c.Status);
        c.CloseByAdmin(null, false, null, Now.AddDays(4));
        Assert.Equal(CaseStatus.Closed, c.Status);
    }
}
