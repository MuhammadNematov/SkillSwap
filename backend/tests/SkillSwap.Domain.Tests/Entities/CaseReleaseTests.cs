using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Возврат обращения в очередь, снятие проверяющего по апелляции и снятие служебного аккаунта (этап 7, шаг 4).</summary>
public class CaseReleaseTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Opener = Guid.NewGuid();
    private static readonly Guid Against = Guid.NewGuid();
    private static readonly Guid Moderator = Guid.NewGuid();

    private static Case Taken()
    {
        var c = Case.Open(CaseType.Conduct, CaseCategory.Insults, Guid.NewGuid(), null, Opener, Against, "Собеседник регулярно оскорбляет меня в чате.", false, false, Now);
        c.Take(Moderator, Now);
        return c;
    }

    [Fact]
    public void A_case_in_progress_goes_back_to_the_queue_keeping_its_reaction_deadline()
    {
        var c = Taken();
        var due = c.DueAt;

        c.ReleaseFromModerator(Moderator);

        Assert.Equal(CaseStatus.Open, c.Status);
        Assert.Null(c.ModeratorId);
        Assert.Equal(due, c.DueAt);
    }

    [Fact]
    public void A_case_waiting_for_answers_goes_back_to_the_queue_and_forgets_the_deadline()
    {
        var c = Taken();
        c.RequestExplanation(Moderator, Now);
        Assert.NotNull(c.ResponseDeadlineAt);

        c.ReleaseFromModerator(Moderator);

        Assert.Equal(CaseStatus.Open, c.Status);
        Assert.Null(c.ResponseDeadlineAt);
        Assert.Null(c.ExplanationReminderSentAt);
        c.Take(Guid.NewGuid(), DateTime.UtcNow);   // следующий модератор берёт обычным способом
        Assert.Equal(CaseStatus.InProgress, c.Status);
    }

    [Fact]
    public void The_first_take_is_remembered_once_and_does_not_change_after_a_release()
    {
        var c = Case.Open(CaseType.Conduct, CaseCategory.Insults, Guid.NewGuid(), null, Opener, Against, "Собеседник регулярно оскорбляет меня в чате.", false, false, Now);
        Assert.Null(c.FirstTakenAt);

        c.Take(Moderator, Now.AddHours(3));
        c.ReleaseFromModerator(Moderator);
        var other = Guid.NewGuid();
        c.Take(other, Now.AddHours(10));

        Assert.Equal(Now.AddHours(3), c.FirstTakenAt);
        Assert.Equal(Moderator, c.FirstTakenById);
        Assert.Equal(other, c.ModeratorId);
    }

    [Fact]
    public void Only_the_holder_can_release_and_only_a_case_in_work()
    {
        var c = Taken();
        var open = Case.Open(CaseType.Conduct, CaseCategory.Insults, Guid.NewGuid(), null, Opener, Against, "Собеседник регулярно оскорбляет меня в чате.", false, false, Now);

        Assert.Throws<InvalidStateTransitionException>(() => c.ReleaseFromModerator(Guid.NewGuid()));
        Assert.Throws<InvalidStateTransitionException>(() => open.ReleaseFromModerator(Moderator));
        c.Resolve(Moderator, CaseDecision.NoViolation, false, "Изучил переписку: нарушения нет, жалоба необоснованна.", Now);
        Assert.Throws<InvalidStateTransitionException>(() => c.ReleaseFromModerator(Moderator));   // решённое не возвращается
    }

    [Fact]
    public void The_reviewer_can_be_cleared_before_the_conclusion_and_a_new_one_assigned()
    {
        var admin = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var appeal = CaseAppeal.Create(Guid.NewGuid(), Opener, "Не согласен с решением: переписка вырвана из контекста.");
        appeal.AssignReviewer(admin, reviewer);

        appeal.ClearReviewer(reviewer);

        Assert.Null(appeal.ReviewerModeratorId);
        appeal.AssignReviewer(admin, Guid.NewGuid());
        Assert.NotNull(appeal.ReviewerModeratorId);
    }

    [Fact]
    public void The_reviewer_cannot_be_cleared_by_a_stranger_or_after_the_conclusion()
    {
        var admin = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        var appeal = CaseAppeal.Create(Guid.NewGuid(), Opener, "Не согласен с решением: переписка вырвана из контекста.");
        appeal.AssignReviewer(admin, reviewer);

        Assert.Throws<InvalidStateTransitionException>(() => appeal.ClearReviewer(Guid.NewGuid()));
        appeal.WriteConclusion(reviewer, "Переписку изучил: нарушение подтверждается, решение оставить.");
        Assert.Throws<InvalidStateTransitionException>(() => appeal.ClearReviewer(reviewer));
    }

    [Fact]
    public void The_admin_who_led_an_appeal_can_be_released_until_the_final_decision()
    {
        var admin = Guid.NewGuid();
        var appeal = CaseAppeal.Create(Guid.NewGuid(), Opener, "Не согласен с решением: переписка вырвана из контекста.");
        appeal.AssignReviewer(admin, Guid.NewGuid());

        Assert.Throws<InvalidStateTransitionException>(() => appeal.ReleaseAdmin(Guid.NewGuid()));   // чужую апелляцию не освободить
        appeal.ReleaseAdmin(admin);

        Assert.Null(appeal.AdminId);
        Assert.Throws<InvalidStateTransitionException>(() => appeal.ReleaseAdmin(admin));   // повторно нечего освобождать
    }

    [Fact]
    public void Removing_a_staff_member_deactivates_the_account_and_keeps_the_role()
    {
        var moderator = User.CreateStaff("m@example.com", "Модератор", UserRole.Moderator);

        moderator.RemoveFromStaff();

        Assert.Equal(UserStatus.Blocked, moderator.Status);
        Assert.Equal(UserRole.Moderator, moderator.Role);
        Assert.Equal("staff_not_active", Assert.Throws<DomainValidationException>(() => moderator.RemoveFromStaff()).Code);
        Assert.Equal("not_staff_account", Assert.Throws<DomainValidationException>(() => User.CreateRegular("a@example.com", "Алишер", new DateOnly(1995, 1, 1), "ru").RemoveFromStaff()).Code);
    }
}
