using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Notifications;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Staff;

/// <summary>Сколько обращений освобождено: <see cref="Returned"/> вернулись в очередь, <see cref="AppealsReturned"/> ждут нового проверяющего по апелляции.</summary>
public sealed record ReleaseSummary(int Returned, int AppealsReturned)
{
    public int Total => Returned + AppealsReturned;
}

/// <summary>
/// Освобождение обращений модератора (раздел 3 ТЗ): при его снятии и когда после привязки личного аккаунта в его работе появился конфликт интересов.
/// Обращение в работе (InProgress, WaitingResponse) возвращается в очередь (Open), апелляция на повторной проверке возвращается админу для назначения другого проверяющего.
/// Все действующие доступы к чату по этим обращениям отзываются, каждое освобождение и каждый отзыв пишутся в аудит. Порядок вызова: <see cref="UsersToLockAsync"/>,
/// блокировка этих пользователей, <see cref="ReleaseAsync"/> в той же транзакции, сохранение и <c>INotificationPublisher.Dispatch()</c> после коммита делает вызывающий.
/// </summary>
public sealed class ModeratorCaseReleaser
{
    /// <summary>Причина освобождения в аудите и уведомлениях: модератора сняли.</summary>
    public const string StaffRemovedReason = "staff_removed";

    /// <summary>Причина освобождения в аудите и уведомлениях: конфликт интересов после привязки личного аккаунта.</summary>
    public const string ConflictReason = "conflict_of_interest";

    private readonly ICaseStore _cases;
    private readonly ModerationService _moderation;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;

    public ModeratorCaseReleaser(ICaseStore cases, ModerationService moderation, IAuditLog audit, INotificationPublisher notify)
    {
        _cases = cases;
        _moderation = moderation;
        _audit = audit;
        _notify = notify;
    }

    /// <summary>Кого заблокировать в транзакции: модератора и участников всех обращений, которые он держит.</summary>
    public async Task<IReadOnlyList<Guid>> UsersToLockAsync(Guid moderatorId, CancellationToken ct)
    {
        var held = await _cases.FindHeldByModeratorAsync(moderatorId, null, tracked: false, ct);
        return held.SelectMany(c => new[] { c.OpenedById, c.AgainstUserId }).Append(moderatorId).Distinct().ToList();
    }

    /// <summary>
    /// Освобождает обращения модератора внутри транзакции (под блокировкой <see cref="UsersToLockAsync"/>). <paramref name="conflictUsers"/> — только обращения с участием
    /// этих пользователей (конфликт интересов), null — все (модератора сняли). <paramref name="actorId"/> — кто в аудите (null: система).
    /// </summary>
    public async Task<ReleaseSummary> ReleaseAsync(
        Guid? actorId, Guid moderatorId, IReadOnlyCollection<Guid>? conflictUsers, string reason, CancellationToken ct)
    {
        var held = await _cases.FindHeldByModeratorAsync(moderatorId, conflictUsers, tracked: true, ct);   // перечитываем уже под блокировкой
        var returned = 0;
        var appealsReturned = 0;
        IReadOnlyList<Guid>? admins = null;
        foreach (var found in held)
        {
            if (found.Status == CaseStatus.AppealReview)
            {
                var appeal = await _cases.FindAppealAsync(found.Id, tracked: true, ct);
                if (appeal is null)
                {
                    continue;
                }

                appeal.ClearReviewer(moderatorId);
                found.EndAppealReview();   // AppealReview → Appealed: ждёт, пока админ назначит другого проверяющего
                await _moderation.RevokeChatAccessAsync(found.Id, actorId, ct);
                _audit.Add(new AuditEntry(actorId, AuditActions.AppealReviewReleased, "Case", found.Id, new { moderatorId, reason }));
                admins ??= await _cases.ListActiveAdminIdsAsync(ct);
                foreach (var admin in admins.Where(a => a != actorId && a != moderatorId))
                {
                    _notify.Add(admin, NotificationType.AppealReviewerReleased, new { caseId = found.Id, exchangeId = found.ExchangeId, reason });
                }

                if (reason == ConflictReason)
                {
                    _notify.Add(moderatorId, NotificationType.CaseTakenAway, new { caseId = found.Id, reason });
                }

                appealsReturned++;
                continue;
            }

            found.ReleaseFromModerator(moderatorId);
            await _moderation.RevokeChatAccessAsync(found.Id, actorId, ct);
            _audit.Add(new AuditEntry(actorId, AuditActions.CaseReleased, "Case", found.Id, new { moderatorId, reason }));
            _notify.Add(found.OpenedById, NotificationType.CaseReturnedToQueue, new { caseId = found.Id, exchangeId = found.ExchangeId, role = nameof(CaseRole.Opener) });
            _notify.Add(found.AgainstUserId, NotificationType.CaseReturnedToQueue, new { caseId = found.Id, exchangeId = found.ExchangeId, role = nameof(CaseRole.Against) });
            if (reason == ConflictReason)
            {
                _notify.Add(moderatorId, NotificationType.CaseTakenAway, new { caseId = found.Id, reason });
            }

            returned++;
        }

        return new ReleaseSummary(returned, appealsReturned);
    }
}
