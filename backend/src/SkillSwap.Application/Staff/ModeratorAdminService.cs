using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Staff;

/// <summary>Нагрузка модератора: сколько обращений он держит и сколько решил за последние 30 дней.</summary>
public sealed record ModeratorLoadDto(int InProgress, int WaitingResponse, int AppealReview, int ResolvedLast30Days);

public sealed record ModeratorListItemDto(Guid Id, string Name, string? Email, bool Removed, DateTime CreatedAt, bool PersonalLinked, ModeratorLoadDto Load);

public sealed record ModeratorListDto(IReadOnlyList<ModeratorListItemDto> Items, int Page, int PageSize, int TotalCount);

/// <param name="Role"><c>Moderator</c> — вёл обращение сам, <c>AppealReviewer</c> — проверяет апелляцию.</param>
public sealed record HeldCaseDto(Guid Id, CaseType Type, CaseStatus Status, string Role, CasePriority Priority, DateTime DueAt, DateTime? ResponseDeadlineAt);

public sealed record ModeratorDto(ModeratorListItemDto Moderator, IReadOnlyList<HeldCaseDto> HeldCases);

public sealed record RemoveModeratorRequest(string? Reason, StepUpConfirmation? Confirmation);

/// <param name="CasesReturned">Сколько обращений вернулось в очередь.</param>
/// <param name="AppealReviewsReturned">Сколько апелляций вернулось админу для назначения другого проверяющего.</param>
/// <param name="PersonalUnlinked">Снята ли привязка личного аккаунта.</param>
public sealed record RemoveModeratorResultDto(ModeratorDto Moderator, int CasesReturned, int AppealReviewsReturned, bool PersonalUnlinked);

/// <param name="AppealsReturned">Сколько апелляций, которые вёл админ, вернулось в общую очередь админов.</param>
public sealed record RemoveAdminResultDto(Guid Id, string Name, bool Removed, int AppealsReturned, bool PersonalUnlinked);

/// <summary>Выборки панели модераторов.</summary>
public interface IModeratorStore
{
    /// <summary>Служебные аккаунты с ролью модератора: действующие или снятые (<paramref name="removed"/>), по имени.</summary>
    Task<(IReadOnlyList<User> Items, int Total)> SearchAsync(bool removed, int page, int pageSize, CancellationToken ct);

    Task<IReadOnlyDictionary<Guid, ModeratorLoadDto>> GetLoadAsync(IReadOnlyCollection<Guid> ids, DateTime since, CancellationToken ct);
}

/// <summary>
/// Модераторы в панели админа (раздел 3 ТЗ): список с нагрузкой, карточка и снятие. Снятие (подтверждение паролем и кодом 2FA, причина в аудит): аккаунт перестаёт действовать,
/// сеансы отзываются и соединения SignalR разрываются, обращения в работе возвращаются в очередь, апелляции на повторной проверке возвращаются админу, все доступы к чату
/// отзываются, привязка личного аккаунта снимается (конфликт интересов остаётся по истории). Модератор снимается админом или суперадмином, себя снять нельзя.
/// </summary>
public sealed class ModeratorAdminService
{
    private readonly IModeratorStore _store;
    private readonly ICaseStore _cases;
    private readonly IProfileStore _profiles;
    private readonly StepUpService _stepUp;
    private readonly ModeratorCaseReleaser _releaser;
    private readonly PersonalLinkService _links;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IRealtimeDisconnector _disconnector;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly TimeProvider _clock;
    private readonly PendingStaffRequestsRevoker _pending;

    public ModeratorAdminService(
        IModeratorStore store, ICaseStore cases, IProfileStore profiles, StepUpService stepUp, ModeratorCaseReleaser releaser, PersonalLinkService links,
        IRefreshTokenRepository refreshTokens, IRealtimeDisconnector disconnector, IUnitOfWork uow, IAuditLog audit, INotificationPublisher notify, TimeProvider clock,
        PendingStaffRequestsRevoker pending)
    {
        _store = store;
        _cases = cases;
        _profiles = profiles;
        _stepUp = stepUp;
        _releaser = releaser;
        _links = links;
        _refreshTokens = refreshTokens;
        _disconnector = disconnector;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _clock = clock;
        _pending = pending;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Список модераторов: действующие (по умолчанию) или снятые, по имени, с нагрузкой.</summary>
    public async Task<ModeratorListDto> ListAsync(Guid adminId, bool removed, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        await RequireAdminAsync(adminId, ct);
        var (items, total) = await _store.SearchAsync(removed, page, pageSize, ct);
        return new ModeratorListDto(await ToItemsAsync(items, ct), page, pageSize, total);
    }

    public async Task<ModeratorDto> GetAsync(Guid adminId, Guid moderatorId, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var user = await FindModeratorAsync(moderatorId, ct);
        var held = await _cases.FindHeldByModeratorAsync(moderatorId, null, tracked: false, ct);
        return new ModeratorDto(
            (await ToItemsAsync([user], ct))[0],
            held.Select(c => new HeldCaseDto(c.Id, c.Type, c.Status, c.Status == CaseStatus.AppealReview ? "AppealReviewer" : "Moderator", c.Priority, c.DueAt, c.ResponseDeadlineAt)).ToList());
    }

    /// <summary>
    /// Снять модератора (пароль и код 2FA, причина 10–500 символов в аудит). 403 <c>staff_role_not_allowed</c>, <c>cannot_change_own_rights</c>; 404 <c>moderator_not_found</c>;
    /// 409 <c>not_a_moderator</c> (админов и суперадминов здесь не снимают), <c>staff_already_removed</c>.
    /// </summary>
    public async Task<RemoveModeratorResultDto> RemoveAsync(Guid adminId, Guid moderatorId, RemoveModeratorRequest request, CancellationToken ct)
    {
        var (summary, _, unlinked) = await RemoveCoreAsync(adminId, moderatorId, request, UserRole.Moderator, ct);
        return new RemoveModeratorResultDto(await GetAsync(adminId, moderatorId, ct), summary.Returned, summary.AppealsReturned, unlinked);
    }

    /// <summary>
    /// Снять админа (раздел 3 ТЗ, правило 4): только суперадмин, пароль и код 2FA, причина 10–500 символов. Последствия те же, что у модератора (аккаунт перестаёт действовать, сеансы
    /// отзываются, соединения разрываются, привязка снимается), а апелляции, которые вёл админ, возвращаются в общую очередь админов. Себя и суперадмина снять нельзя.
    /// 403 <c>staff_role_not_allowed</c> (вы не суперадмин или цель суперадмин), <c>cannot_change_own_rights</c>; 404 <c>admin_not_found</c>; 409 <c>not_an_admin</c>, <c>staff_already_removed</c>.
    /// </summary>
    public async Task<RemoveAdminResultDto> RemoveAdminAsync(Guid actorId, Guid adminId, RemoveModeratorRequest request, CancellationToken ct)
    {
        var (_, appeals, unlinked) = await RemoveCoreAsync(actorId, adminId, request, UserRole.Admin, ct);
        var user = (await _profiles.FindUserAsync(adminId, ct))!;
        return new RemoveAdminResultDto(user.Id, user.Name, user.Status != UserStatus.Active, appeals, unlinked);
    }

    private async Task<(ReleaseSummary Summary, int AdminAppeals, bool Unlinked)> RemoveCoreAsync(
        Guid actorId, Guid targetId, RemoveModeratorRequest request, UserRole expected, CancellationToken ct)
    {
        var actor = await RequireAdminAsync(actorId, ct);
        var notFound = expected == UserRole.Admin ? "admin_not_found" : "moderator_not_found";
        var target = await _profiles.FindUserAsync(targetId, ct);
        if (target is null || target.IsRegular || target.Role == UserRole.User)
        {
            throw new NotFoundException(notFound, "Сотрудник не найден.");
        }

        StaffRules.RequireNotSelf(actorId, targetId);
        StaffRules.RequireCanManage(actor.Role, target.Role);   // суперадмина не снимает никто, админа только суперадмин
        if (target.Role != expected)
        {
            throw new ConflictException(expected == UserRole.Admin ? "not_an_admin" : "not_a_moderator", expected == UserRole.Admin ? "Здесь снимают только админов." : "Здесь снимают только модераторов.");
        }

        if (target.Status != UserStatus.Active)
        {
            throw new ConflictException("staff_already_removed", "Сотрудник уже снят.");
        }

        var reason = Case.CleanText(request.Reason, 10, 500, "removal_reason");
        await _stepUp.RequireAsync(actorId, request.Confirmation, ct);

        ReleaseSummary summary = new(0, 0);
        var adminAppeals = 0;
        var unlinked = false;
        await _uow.ExecuteAsync(async t =>
        {
            var lockIds = (await _releaser.UsersToLockAsync(targetId, t)).Append(actorId).ToList();
            if (target.LinkedUserId is { } personal)
            {
                lockIds.Add(personal);
            }

            await _uow.LockUsersAsync(lockIds, t);
            var staff = await _profiles.FindUserForUpdateAsync(targetId, t) ?? throw new NotFoundException(notFound, "Сотрудник не найден.");
            staff.RemoveFromStaff();   // уже снят (гонка двух админов): 400 staff_not_active
            var now = Now();
            await _refreshTokens.RevokeAllForUserAsync(targetId, now, t);
            if (expected == UserRole.Admin)
            {
                adminAppeals = await _releaser.ReleaseAdminAppealsAsync(actorId, targetId, t);
            }
            else
            {
                summary = await _releaser.ReleaseAsync(actorId, targetId, null, ModeratorCaseReleaser.StaffRemovedReason, t);
            }

            unlinked = await _links.UnlinkForRemovalAsync(actor, targetId, t);
            var revoked = await _pending.RevokeAsync(actorId, targetId, now, PendingStaffRequestsRevoker.StaffRemovedReason, t);   // приглашения снятого и его запросы на привязку, без писем
            _audit.Add(new AuditEntry(actorId, StaffAuditActions.StaffRemoved, "User", targetId, new
            {
                reason,
                role = staff.Role,
                casesReturned = summary.Returned,
                appealReviewsReturned = summary.AppealsReturned,
                appealsReturnedToAdmins = adminAppeals,
                personalUnlinked = unlinked,
                invitationsRevoked = revoked.Invitations,
                linkRequestsCancelled = revoked.LinkRequests,
            }));
            await _cases.SaveAsync(t);
            return 0;
        }, ct);

        _notify.Dispatch();
        await _disconnector.DisconnectUserAsync(targetId, ct);
        return (summary, adminAppeals, unlinked);
    }

    // ---------- внутреннее ----------

    private async Task<IReadOnlyList<ModeratorListItemDto>> ToItemsAsync(IReadOnlyList<User> users, CancellationToken ct)
    {
        var load = await _store.GetLoadAsync(users.Select(u => u.Id).ToList(), Now().AddDays(-30), ct);
        return users.Select(u => new ModeratorListItemDto(
            u.Id, u.Name, u.Email, u.Status != UserStatus.Active, u.CreatedAt, u.LinkedUserId is not null, load.GetValueOrDefault(u.Id) ?? new ModeratorLoadDto(0, 0, 0, 0))).ToList();
    }

    private async Task<User> FindModeratorAsync(Guid id, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(id, ct);
        if (user is null || user.IsRegular || user.Role != UserRole.Moderator)
        {
            throw new NotFoundException("moderator_not_found", "Модератор не найден.");
        }

        return user;
    }

    private async Task<User> RequireAdminAsync(Guid adminId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(adminId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role is not (UserRole.Admin or UserRole.SuperAdmin) || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("admin_required", "Доступно только администратору.");
        }

        return user;
    }
}
