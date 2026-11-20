using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>
/// Апелляция (раздел 8.2 ТЗ, пункт 5): участник подаёт её один раз в течение 7 дней после решения; админ при необходимости назначает другого модератора на повторную
/// проверку чата, тот пишет заключение (его доступ к чату закрывается), окончательное решение принимает админ (<see cref="AdminCaseService"/>).
/// </summary>
public sealed class AppealService
{
    private readonly ICaseStore _cases;
    private readonly CaseService _caseService;
    private readonly ModerationService _moderation;
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly TimeProvider _clock;

    public AppealService(
        ICaseStore cases, CaseService caseService, ModerationService moderation, IProfileStore profiles, IUnitOfWork uow, IAuditLog audit, INotificationPublisher notify, TimeProvider clock)
    {
        _cases = cases;
        _caseService = caseService;
        _moderation = moderation;
        _profiles = profiles;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Подать апелляцию (Resolved → Appealed). Любая из двух сторон, один раз, пока не прошло 7 дней после решения. Второй стороне и админам приходит уведомление.</summary>
    public async Task<CaseDto> FileAsync(Guid userId, Guid caseId, FileAppealRequest request, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Апелляцию подают обычные аккаунты.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }

        var peek = await _cases.FindAsync(caseId, tracked: false, ct);
        if (peek is null || !peek.IsParty(userId) || peek.Status is CaseStatus.Open or CaseStatus.Withdrawn || peek.ModeratorId is null)
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        var admins = await _cases.ListActiveAdminIdsAsync(ct);
        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([peek.ModeratorId.Value, peek.OpenedById, peek.AgainstUserId], token);
            var found = await _cases.FindAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            var appeal = CaseAppeal.Create(caseId, userId, request.Reason);   // короткая причина: 400
            found.Appeal(userId, Now());   // не Resolved: 409 invalid_state_transition; больше 7 дней: 400 appeal_window_closed

            _cases.AddAppeal(appeal);
            _audit.Add(new AuditEntry(userId, AuditActions.CaseAppealed, "Case", caseId));
            _notify.Add(found.PartnerOf(userId), NotificationType.CaseAppealed, new { caseId, exchangeId = found.ExchangeId });
            foreach (var admin in admins)
            {
                _notify.Add(admin, NotificationType.CaseAppealed, new { caseId, exchangeId = found.ExchangeId });
            }

            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        return await _caseService.GetAsync(userId, caseId, ct);
    }

    /// <summary>
    /// Заключение по повторной проверке (AppealReview → Appealed). Только назначенный проверяющий; заключение одно, 20–4000 символов. Его доступ к чату закрывается
    /// в той же транзакции (аудит <c>ChatAccessRevoked</c>), админ получает уведомление.
    /// </summary>
    public async Task<ModerationCaseDto> ConcludeAsync(Guid moderatorId, Guid caseId, AppealConclusionRequest request, CancellationToken ct)
    {
        var moderator = await _profiles.FindUserAsync(moderatorId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (moderator.Role != UserRole.Moderator || moderator.IsRegular || moderator.Status != UserStatus.Active)
        {
            throw new ForbiddenException("moderator_required", "Доступно только модератору.");
        }

        var peek = await _cases.FindAsync(caseId, tracked: false, ct);
        var peekAppeal = peek is null ? null : await _cases.FindAppealAsync(caseId, tracked: false, ct);
        if (peek is null || peekAppeal is null || peekAppeal.ReviewerModeratorId != moderatorId)
        {
            throw new NotFoundException("case_not_found", "Обращение не найдено.");
        }

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([moderatorId, peek.OpenedById, peek.AgainstUserId], token);
            var found = await _cases.FindAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            var appeal = await _cases.FindAppealAsync(caseId, tracked: true, token) ?? throw new NotFoundException("case_not_found", "Обращение не найдено.");
            if (found.Status != CaseStatus.AppealReview)
            {
                throw new ConflictException("invalid_state_transition", "Повторная проверка по этому обращению не идёт.");
            }

            appeal.WriteConclusion(moderatorId, request.Conclusion);   // повтор: 400 appeal_conclusion_already_written; короткий текст: 400
            found.EndAppealReview();
            await _moderation.RevokeChatAccessAsync(caseId, moderatorId, token);
            _audit.Add(new AuditEntry(moderatorId, AuditActions.AppealConcluded, "Case", caseId));
            if (appeal.AdminId is { } admin)
            {
                _notify.Add(admin, NotificationType.AppealConclusionReady, new { caseId });
            }

            await _cases.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        return await _moderation.GetAsync(moderatorId, caseId, ct);
    }
}
