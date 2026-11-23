using SkillSwap.Application.Moderation;

namespace SkillSwap.Application.Staff;

/// <summary>Что отозвано у заблокированного аккаунта или снятого сотрудника.</summary>
public sealed record RevokedRequests(int Invitations, int LinkRequests)
{
    public static readonly RevokedRequests None = new(0, 0);
}

/// <summary>
/// Отзыв неразобранных приглашений и запросов на привязку, когда аккаунт заблокирован или сотрудник снят (этап 7, шаг 9). Иначе ссылки из писем продолжали бы работать
/// от имени того, кого уже нет: по приглашению снятого админа создался бы новый служебный аккаунт, а заблокированный личный аккаунт можно было бы привязать к чужому служебному.
/// Отзывается всё, что ещё действует: приглашения, отправленные этим аккаунтом, и запросы на привязку, где он служебный или личный аккаунт. Письма получателям **не уходят**,
/// уведомлений тоже нет: ссылка просто перестаёт работать. Вызывается внутри транзакции вызывающего (сохраняет он); каждое действие пишется в аудит.
/// </summary>
public sealed class PendingStaffRequestsRevoker
{
    /// <summary>Причина в аудите: сотрудник снят.</summary>
    public const string StaffRemovedReason = "staff_removed";

    /// <summary>Причина в аудите: аккаунт заблокирован.</summary>
    public const string AccountBlockedReason = "account_blocked";

    private readonly IStaffInvitationStore _invitations;
    private readonly IPersonalLinkStore _links;
    private readonly IAuditLog _audit;

    public PendingStaffRequestsRevoker(IStaffInvitationStore invitations, IPersonalLinkStore links, IAuditLog audit)
    {
        _invitations = invitations;
        _links = links;
        _audit = audit;
    }

    /// <param name="actorId">Админ, который заблокировал или снял аккаунт (в аудит).</param>
    /// <param name="userId">Заблокированный аккаунт или снятый сотрудник.</param>
    /// <param name="now">Текущее время.</param>
    /// <param name="reason"><see cref="StaffRemovedReason"/> или <see cref="AccountBlockedReason"/>.</param>
    /// <param name="ct">Отмена.</param>
    public async Task<RevokedRequests> RevokeAsync(Guid actorId, Guid userId, DateTime now, string reason, CancellationToken ct)
    {
        var invitations = 0;
        foreach (var invitation in await _invitations.ListPendingByInviterAsync(userId, now, ct))
        {
            if (invitation.Revoke(now))
            {
                invitations++;
                _audit.Add(new AuditEntry(actorId, StaffAuditActions.StaffInvitationRevoked, "StaffInvitation", invitation.Id, new { email = invitation.Email, reason, inviterId = userId }));
            }
        }

        var links = 0;
        foreach (var request in await _links.ListPendingInvolvingAsync(userId, now, ct))
        {
            if (request.Cancel(now))
            {
                links++;
                _audit.Add(new AuditEntry(actorId, StaffAuditActions.PersonalLinkRequestCancelled, "PersonalLinkRequest", request.Id, new { reason, staffId = request.StaffId, personalUserId = request.PersonalUserId }));
            }
        }

        return invitations + links == 0 ? RevokedRequests.None : new RevokedRequests(invitations, links);
    }
}
