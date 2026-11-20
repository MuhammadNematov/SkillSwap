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

    public ModeratorAdminService(
        IModeratorStore store, ICaseStore cases, IProfileStore profiles, StepUpService stepUp, ModeratorCaseReleaser releaser, PersonalLinkService links,
        IRefreshTokenRepository refreshTokens, IRealtimeDisconnector disconnector, IUnitOfWork uow, IAuditLog audit, INotificationPublisher notify, TimeProvider clock)
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
        var admin = await RequireAdminAsync(adminId, ct);
        var target = await FindModeratorAsync(moderatorId, ct, anyRole: true);
        StaffRules.RequireNotSelf(adminId, moderatorId);
        StaffRules.RequireCanManage(admin.Role, target.Role);
        if (target.Role != UserRole.Moderator)
        {
            throw new ConflictException("not_a_moderator", "Здесь снимают только модераторов.");
        }

        if (target.Status != UserStatus.Active)
        {
            throw new ConflictException("staff_already_removed", "Модератор уже снят.");
        }

        var reason = Case.CleanText(request.Reason, 10, 500, "removal_reason");
        await _stepUp.RequireAsync(adminId, request.Confirmation, ct);

        ReleaseSummary summary = new(0, 0);
        var unlinked = false;
        await _uow.ExecuteAsync(async t =>
        {
            var lockIds = (await _releaser.UsersToLockAsync(moderatorId, t)).Append(adminId).ToList();
            if (target.LinkedUserId is { } personal)
            {
                lockIds.Add(personal);
            }

            await _uow.LockUsersAsync(lockIds, t);
            var staff = await _profiles.FindUserForUpdateAsync(moderatorId, t) ?? throw new NotFoundException("moderator_not_found", "Модератор не найден.");
            staff.RemoveFromStaff();   // уже снят (гонка двух админов): 400 staff_not_active
            var now = Now();
            await _refreshTokens.RevokeAllForUserAsync(moderatorId, now, t);
            summary = await _releaser.ReleaseAsync(adminId, moderatorId, null, ModeratorCaseReleaser.StaffRemovedReason, t);
            unlinked = await _links.UnlinkForRemovalAsync(admin, moderatorId, t);
            _audit.Add(new AuditEntry(adminId, StaffAuditActions.StaffRemoved, "User", moderatorId, new
            {
                reason,
                role = staff.Role,
                casesReturned = summary.Returned,
                appealReviewsReturned = summary.AppealsReturned,
                personalUnlinked = unlinked,
            }));
            await _cases.SaveAsync(t);
            return 0;
        }, ct);

        _notify.Dispatch();
        await _disconnector.DisconnectUserAsync(moderatorId, ct);
        return new RemoveModeratorResultDto(await GetAsync(adminId, moderatorId, ct), summary.Returned, summary.AppealsReturned, unlinked);
    }

    // ---------- внутреннее ----------

    private async Task<IReadOnlyList<ModeratorListItemDto>> ToItemsAsync(IReadOnlyList<User> users, CancellationToken ct)
    {
        var load = await _store.GetLoadAsync(users.Select(u => u.Id).ToList(), Now().AddDays(-30), ct);
        return users.Select(u => new ModeratorListItemDto(
            u.Id, u.Name, u.Email, u.Status != UserStatus.Active, u.CreatedAt, u.LinkedUserId is not null, load.GetValueOrDefault(u.Id) ?? new ModeratorLoadDto(0, 0, 0, 0))).ToList();
    }

    private async Task<User> FindModeratorAsync(Guid id, CancellationToken ct, bool anyRole = false)
    {
        var user = await _profiles.FindUserAsync(id, ct);
        if (user is null || user.IsRegular || user.Role == UserRole.User || (!anyRole && user.Role != UserRole.Moderator))
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
