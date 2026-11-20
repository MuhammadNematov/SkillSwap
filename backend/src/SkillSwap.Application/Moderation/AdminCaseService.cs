using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Reviews;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>
/// Обращения и апелляции глазами админа (раздел 3 и 8.2 ТЗ). Админ видит материалы обращения и приложенные сообщения, но не весь чат: доступа к переписке
/// у него нет никогда, а при необходимости проверить чат он назначает другого модератора. Окончательное решение (оставить, изменить, отменить) принимает админ;
/// отмена и замена возвращают последствия прежнего решения.
/// </summary>
public sealed class AdminCaseService
{
    private readonly ICaseStore _cases;
    private readonly ICaseMessageStore _caseMessages;
    private readonly IMessageStore _messages;
    private readonly IReviewStore _reviews;
    private readonly IExchangeStore _exchanges;
    private readonly IChatAccessGrantStore _grants;
    private readonly ModerationService _moderation;
    private readonly ReviewScheduler _reviewScheduler;
    private readonly Sessions.LessonSessionService _lessons;
    private readonly CaseCrossChecks _checks;
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly IMessageCipher _cipher;
    private readonly TimeProvider _clock;
    private readonly CaseConsequences _consequences;

    public AdminCaseService(
        ICaseStore cases, ICaseMessageStore caseMessages, IMessageStore messages, IReviewStore reviews, IExchangeStore exchanges, IChatAccessGrantStore grants,
        ModerationService moderation, ReviewScheduler reviewScheduler, Sessions.LessonSessionService lessons, CaseCrossChecks checks, IProfileStore profiles, IUnitOfWork uow,
        IAuditLog audit, INotificationPublisher notify, IMessageCipher cipher, TimeProvider clock, CaseConsequences consequences)
    {
        _cases = cases;
        _caseMessages = caseMessages;
        _messages = messages;
        _reviews = reviews;
        _exchanges = exchanges;
        _grants = grants;
        _moderation = moderation;
        _reviewScheduler = reviewScheduler;
        _lessons = lessons;
        _checks = checks;
        _profiles = profiles;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _cipher = cipher;
        _clock = clock;
        _consequences = consequences;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    // ---------- чтение ----------

    public async Task<AdminCaseListDto> ListAsync(Guid adminId, CaseStatus? status, CaseType? type, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        await RequireAdminAsync(adminId, ct);
        var (items, total) = await _cases.ListForAdminAsync(status, type, page, pageSize, ct);
        return new AdminCaseListDto(await ToDtosAsync(items, ct), page, pageSize, total);
    }

    /// <summary>Материалы обращения: описание, оспариваемый отзыв, приложенные сообщения (удалённые и с прежними версиями), пояснения, предупреждения, запросы на бан. Просмотр пишется в аудит.</summary>
    public async Task<AdminCaseDetailDto> GetAsync(Guid adminId, Guid caseId, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var found = await _cases.FindAsync(caseId, tracked: false, ct);
        if (found is null || found.Status == CaseStatus.Withdrawn)
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        var dto = (await ToDtosAsync([found], ct))[0];
        ModerationReviewDto? review = null;
        if (found.ReviewId is { } reviewId && await _reviews.FindAsync(reviewId, ct) is { } r)
        {
            review = new ModerationReviewDto(r.Id, r.AuthorId, r.TargetId, r.ScoreKnowledge, r.ScoreExplaining, r.ScorePunctuality, r.ScorePoliteness, r.Average, r.Text, r.Status);
        }

        var evidenceIds = (await _cases.GetEvidenceIdsAsync([caseId], ct)).GetValueOrDefault(caseId) ?? [];
        var evidence = await EvidenceAsync(evidenceIds, ct);
        var thread = await _caseMessages.ListAsync(caseId, null, ct);
        var answered = await _caseMessages.AnsweredQuestionIdsAsync(thread.Where(m => m.InReplyToId is null).Select(m => m.Id).ToList(), ct);
        var explanations = thread.Select(m => new ExplanationDto(
            m.Id, m.InReplyToId is null ? ExplanationKind.Question : ExplanationKind.Answer, m.SenderId, m.RecipientId, m.Text, m.InReplyToId,
            m.InReplyToId is null && answered.Contains(m.Id), m.CreatedAt)).ToList();
        var warnings = (await _cases.ListWarningsForCaseReadOnlyAsync(caseId, ct)).Select(w => new WarningDto(w.Id, w.UserId, w.IssuedById, w.Reason, w.CreatedAt, w.ExpiresAt, w.RevokedAt)).ToList();
        var bans = await BanDtosAsync(await _cases.FindBanRequestsForCaseAsync(caseId, ct), ct);

        await _audit.WriteAsync(new AuditEntry(adminId, AuditActions.CaseMaterialsRead, "Case", caseId, new { evidence = evidence.Count }), ct);
        return new AdminCaseDetailDto(dto, review, evidence, explanations, warnings, bans);
    }

    public async Task<IReadOnlyList<ModeratorOptionDto>> ListModeratorsAsync(Guid adminId, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        return (await _cases.ListModeratorsAsync(ct)).Select(u => new ModeratorOptionDto(u.Id, u.Name)).ToList();
    }

    // ---------- повторная проверка ----------

    /// <summary>
    /// Назначить другого модератора на повторную проверку чата (Appealed → AppealReview). Проверяющий не автор решения, не участник и без конфликта интересов
    /// (личный аккаунт, бывшие партнёры). Ему выдаётся доступ к чату этого обмена, обе стороны получают одно уведомление об этом.
    /// </summary>
    public async Task<AdminCaseDto> AssignReviewerAsync(Guid adminId, Guid caseId, AssignReviewerRequest request, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var peek = await _cases.FindAsync(caseId, tracked: false, ct);
        if (peek is null || await _cases.FindAppealAsync(caseId, tracked: false, ct) is null)
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        var reviewer = await _profiles.FindUserAsync(request.ModeratorId, ct) ?? throw new NotFoundException("moderator_not_found", "Модератор не найден.");
        if (reviewer.Role != UserRole.Moderator || reviewer.IsRegular || reviewer.Status != UserStatus.Active)
        {
            throw new ConflictException("not_a_moderator", "Проверять апелляцию может только действующий модератор.");
        }

        if (await _checks.HasConflictAsync(reviewer, peek, ct))
        {
            throw new ConflictException("conflict_of_interest", "Этот модератор не может проверять обращение: конфликт интересов.");
        }

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([adminId, reviewer.Id, peek.ModeratorId ?? reviewer.Id, peek.OpenedById, peek.AgainstUserId], token);
            var found = await _cases.FindAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            var appeal = await _cases.FindAppealAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            var now = Now();
            found.BeginAppealReview(reviewer.Id);   // не Appealed: 409; автор решения: 400 appeal_reviewer_must_differ; участник: 400 conflict_of_interest
            appeal.AssignReviewer(adminId, reviewer.Id);   // уже назначен: 400 appeal_reviewer_already_assigned

            _grants.Add(ChatAccessGrant.Create(caseId, reviewer.Id, found.ExchangeId, GrantReason.AppealReview, now));
            _audit.Add(new AuditEntry(adminId, AuditActions.AppealReviewAssigned, "Case", caseId, new { reviewerId = reviewer.Id }));
            _audit.Add(new AuditEntry(adminId, AuditActions.ChatAccessGranted, "Exchange", found.ExchangeId, new { caseId, moderatorId = reviewer.Id, reason = GrantReason.AppealReview }));
            _notify.Add(found.OpenedById, NotificationType.CaseAppealReviewAssigned, new { caseId, exchangeId = found.ExchangeId, role = nameof(CaseRole.Opener) });
            _notify.Add(found.AgainstUserId, NotificationType.CaseAppealReviewAssigned, new { caseId, exchangeId = found.ExchangeId, role = nameof(CaseRole.Against) });
            _notify.Add(reviewer.Id, NotificationType.AppealReviewAssigned, new { caseId, exchangeId = found.ExchangeId });
            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        return (await ToDtosAsync([(await _cases.FindAsync(caseId, tracked: false, ct))!], ct))[0];
    }

    // ---------- окончательное решение ----------

    /// <summary>
    /// Окончательное решение админа (Appealed → Closed): <c>Upheld</c> решение остаётся, <c>Overturned</c> отменяет его, <c>Changed</c> заменяет другим. Отмена и замена откатывают
    /// прежние последствия (отзыв возвращается в рейтинг, предупреждение отзывается, обмен размораживается), замена применяет новые. Принудительное завершение обмена и
    /// уже одобренную блокировку откатить нельзя. Обе стороны получают уведомление и письмо.
    /// </summary>
    public async Task<AdminCaseDto> DecideAsync(Guid adminId, Guid caseId, DecideAppealRequest request, CancellationToken ct)
    {
        if ((request.Outcome == AppealOutcome.Changed) != (request.NewDecision is not null))
        {
            throw new BadRequestException("appeal_final_decision_mismatch", "Новое решение указывается только при замене решения.");
        }

        await RequireAdminAsync(adminId, ct);
        var peek = await _cases.FindAsync(caseId, tracked: false, ct);
        if (peek is null || await _cases.FindAppealAsync(caseId, tracked: false, ct) is null)
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        ConsequenceResult result = ConsequenceResult.None;
        bool warningRevoked = false;
        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync(new[] { adminId, peek.ModeratorId, peek.OpenedById, peek.AgainstUserId }.OfType<Guid>(), token);
            var found = await _cases.FindAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            var appeal = await _cases.FindAppealAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            if (found.Status == CaseStatus.AppealReview)
            {
                throw new ConflictException("appeal_review_in_progress", "Идёт повторная проверка: дождитесь заключения модератора.");
            }

            if (found.Status != CaseStatus.Appealed)
            {
                throw new ConflictException("invalid_state_transition", "По этому обращению нет апелляции, ожидающей решения.");
            }

            var now = Now();
            var oldDecision = found.Decision!.Value;
            var oldWarned = oldDecision == CaseDecision.Warning || found.AlsoWarned;
            var neutral = found.Type == CaseType.ReviewDispute ? CaseDecision.ReviewKept : CaseDecision.NoViolation;

            switch (request.Outcome)
            {
                case AppealOutcome.Upheld:
                    appeal.Decide(adminId, AppealOutcome.Upheld, null, now);
                    found.CloseByAdmin(null, false, null, now);
                    break;
                case AppealOutcome.Overturned:
                    if (oldDecision == neutral && !oldWarned)
                    {
                        throw new ConflictException("nothing_to_overturn", "У этого решения нет последствий, которые можно отменить: оставьте его или замените.");
                    }

                    appeal.Decide(adminId, AppealOutcome.Overturned, null, now);
                    found.CloseByAdmin(neutral, false, request.Reason, now);
                    warningRevoked = await _consequences.ReverseAsync(found, oldDecision, oldWarned, adminId, now, token);
                    result = await _consequences.ApplyAsync(found, adminId, now, token);
                    break;
                default:
                    appeal.Decide(adminId, AppealOutcome.Changed, request.NewDecision, now);   // новое решение не указано или указано не при замене: 400
                    found.CloseByAdmin(request.NewDecision, request.AlsoWarned, request.Reason, now);   // не подходит типу, предупреждение не сочетается, короткое обоснование: 400
                    warningRevoked = await _consequences.ReverseAsync(found, oldDecision, oldWarned, adminId, now, token);
                    result = await _consequences.ApplyAsync(found, adminId, now, token);
                    break;
            }

            await _moderation.RevokeChatAccessAsync(caseId, adminId, token);
            _audit.Add(new AuditEntry(adminId, AuditActions.AppealDecided, "Case", caseId, new { outcome = request.Outcome, decision = found.Decision, comment = request.Reason }));
            var finalWarned = request.Outcome != AppealOutcome.Upheld && result.Warned;
            foreach (var (user, role) in new[] { (found.OpenedById, CaseRole.Opener), (found.AgainstUserId, CaseRole.Against) })
            {
                _notify.Add(user, NotificationType.AppealDecided, new
                {
                    caseId,
                    exchangeId = found.ExchangeId,
                    role = role.ToString(),
                    outcome = request.Outcome.ToString(),
                    decision = found.Decision.ToString(),
                    alsoWarned = request.Outcome == AppealOutcome.Upheld ? oldWarned : finalWarned,
                    youWereWarned = finalWarned && role == CaseRole.Against,
                    warningRevoked = warningRevoked && role == CaseRole.Against,
                });
            }

            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        await _lessons.PublishUpdatesAsync(result.CancelledLessons);
        if (result.ExchangeClosedAt is { } closed)
        {
            _reviewScheduler.OnExchangeClosed(peek.ExchangeId, closed);
        }

        return (await ToDtosAsync([(await _cases.FindAsync(caseId, tracked: false, ct))!], ct))[0];
    }

    // ---------- внутреннее ----------

    private async Task<IReadOnlyList<AdminCaseDto>> ToDtosAsync(IReadOnlyList<Case> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var exchanges = await _exchanges.GetViewsAsync(items.Select(c => c.ExchangeId).Distinct().ToList(), ct);
        var people = exchanges.Values.SelectMany(e => new[] { e.UserA, e.UserB }).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First() with { AvatarUrl = null });
        var appeals = await _cases.GetAppealsAsync(items.Select(c => c.Id).ToList(), ct);
        return items.Select(c =>
        {
            var appeal = appeals.GetValueOrDefault(c.Id);
            return new AdminCaseDto(
                c.Id, c.Type, c.Category, c.Status, c.Priority, c.ExchangeId, c.ReviewId, people[c.OpenedById], people[c.AgainstUserId], c.Description, c.FromCall, c.CreatedAt,
                c.ModeratorId, c.Decision, c.AlsoWarned, c.DecisionReason, c.ResolvedAt, c.ClosedAt,
                appeal is null ? null : new StaffAppealDto(
                    appeal.AppellantId, appeal.Reason, appeal.CreatedAt, appeal.AdminId, appeal.ReviewerModeratorId, appeal.ReviewerConclusion, appeal.Outcome, appeal.FinalDecision, appeal.DecidedAt));
        }).ToList();
    }

    private async Task<IReadOnlyList<ModerationMessageDto>> EvidenceAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var items = await _messages.GetManyAsync(ids, ct);
        var revisions = await _messages.ListRevisionsAsync(items.Select(m => m.Id).ToList(), ct);
        return items.Select(m => new ModerationMessageDto(
            m.Id, m.SenderId, m.Type, m.EncryptedContent is null ? null : _cipher.Decrypt(m.EncryptedContent), m.PaymentWarning, m.AttachmentId, null,
            m.CreatedAt, m.EditedAt, m.DeletedAt, m.ReadAt, true,
            revisions.TryGetValue(m.Id, out var list) ? list.Select(r => new MessageRevisionDto(_cipher.Decrypt(r.PreviousEncryptedContent), r.ChangedAt)).ToList() : [])).ToList();
    }

    internal async Task<IReadOnlyList<BanRequestDto>> BanDtosAsync(IReadOnlyList<BanRequest> items, CancellationToken ct)
    {
        var users = await _profiles.FindUsersAsync(items.Select(b => b.UserId).Distinct().ToList(), ct);
        return items.Select(b => new BanRequestDto(
            b.Id, b.UserId, users.TryGetValue(b.UserId, out var u) ? (u.Status == UserStatus.Deleted ? User.DeletedDisplayName : u.Name) : User.DeletedDisplayName,
            b.CaseId, b.RequestedById, b.Reason, b.Status, b.DecidedById, b.DecidedAt, b.CreatedAt)).ToList();
    }

    private async Task RequireAdminAsync(Guid adminId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(adminId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role is not (UserRole.Admin or UserRole.SuperAdmin) || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("admin_required", "Доступно только администратору.");
        }
    }
}

/// <summary>Проверки, общие для модераторской и админской частей: конфликт интересов проверяющего.</summary>
public sealed class CaseCrossChecks
{
    private readonly ICaseStore _cases;

    public CaseCrossChecks(ICaseStore cases)
    {
        _cases = cases;
    }

    /// <summary>Есть ли у модератора конфликт интересов с обращением: его личный аккаунт или бывший партнёр личного аккаунта среди участников.</summary>
    public async Task<bool> HasConflictAsync(User moderator, Case value, CancellationToken ct)
    {
        if (moderator.LinkedUserId is not { } personal)
        {
            return false;
        }

        var conflicting = await _cases.GetConflictUserIdsAsync(personal, ct);
        return conflicting.Contains(value.OpenedById) || conflicting.Contains(value.AgainstUserId);
    }
}
