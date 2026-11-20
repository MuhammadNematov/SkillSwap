using System.Text.Json;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.AdminUsers;

/// <summary>Причина блокировки, разблокировки или снятия предупреждения: 10–500 символов, пишется в аудит (пользователю не показывается).</summary>
public sealed record AdminUserActionRequest(string? Reason);

/// <summary>Строка списка пользователей (обычные аккаунты). Дату рождения не отдаём: только возрастная группа.</summary>
public sealed record AdminUserListItemDto(
    Guid Id, string Name, string? Email, UserStatus Status, string AgeGroup, DateTime CreatedAt, DateTime? LastActiveAt, decimal RatingAvg, int RatingCount, int ActiveWarnings,
    int OpenCases);

public sealed record AdminUserListDto(IReadOnlyList<AdminUserListItemDto> Items, int Page, int PageSize, int TotalCount);

public sealed record AdminWarningDto(Guid Id, Guid CaseId, Guid IssuedById, string Reason, DateTime IssuedAt, DateTime ExpiresAt, DateTime? RevokedAt, bool IsActive);

/// <param name="Role">Кем пользователь был в обращении: <c>Opener</c> (подал) или <c>Against</c> (подано на него).</param>
public sealed record AdminUserCaseDto(Guid Id, CaseType Type, CaseStatus Status, CaseDecision? Decision, string Role, DateTime CreatedAt);

/// <param name="Action"><c>UserBlocked</c> или <c>UserUnblocked</c>.</param>
/// <param name="Source">Блокировка: <c>manual</c> (админом) или <c>ban_request</c> (одобренный запрос на бан); у разблокировки пусто.</param>
public sealed record BlockEventDto(string Action, DateTime At, Guid? ById, string? Reason, string? Source);

/// <summary>Карточка пользователя для админа: профиль, предупреждения, обращения, запросы на бан, история блокировок.</summary>
public sealed record AdminUserDto(
    AdminUserListItemDto User,
    string UiLanguage,
    string TimeZone,
    bool EmailConfirmed,
    int ActiveExchanges,
    IReadOnlyList<AdminWarningDto> Warnings,
    IReadOnlyList<AdminUserCaseDto> Cases,
    IReadOnlyList<BanRequestDto> BanRequests,
    IReadOnlyList<BlockEventDto> BlockHistory);

/// <summary>Пользователь вместе со счётчиками для списка и карточки.</summary>
public sealed record AdminUserRow(User User, int ActiveWarnings, int OpenCases);

/// <summary>Выборки для панели пользователей админа.</summary>
public interface IAdminUserStore
{
    Task<(IReadOnlyList<AdminUserRow> Items, int Total)> SearchAsync(string? search, UserStatus? status, DateTime now, int page, int pageSize, CancellationToken ct);

    Task<AdminUserRow?> GetRowAsync(Guid id, DateTime now, CancellationToken ct);

    Task<IReadOnlyList<Warning>> ListWarningsAsync(Guid userId, CancellationToken ct);

    /// <summary>Предупреждение для изменения (с отслеживанием).</summary>
    Task<Warning?> FindWarningAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Case>> ListCasesAsync(Guid userId, int limit, CancellationToken ct);

    /// <summary>Записи аудита о блокировках и разблокировках пользователя, новые сверху.</summary>
    Task<IReadOnlyList<AuditLog>> ListBlockEventsAsync(Guid userId, int limit, CancellationToken ct);

    Task<int> CountActiveExchangesAsync(Guid userId, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}

/// <summary>
/// Пользователи в панели админа (раздел 3 ТЗ): поиск, карточка, блокировка и разблокировка, снятие предупреждения. Блокировка вручную даёт **те же последствия**, что одобренный запрос
/// на бан (общий путь <see cref="AccountBlocker"/>); необработанные запросы на бан по этому пользователю закрываются как одобренные. Разблокировка возвращает аккаунт в статус
/// <c>Active</c>, прерванные обмены не возвращает, пользователю уходит письмо. Обычные аккаунты; служебными управляют отдельные разделы панели.
/// </summary>
public sealed class AdminUserService
{
    private readonly IAdminUserStore _store;
    private readonly ICaseStore _cases;
    private readonly IProfileStore _profiles;
    private readonly AccountBlocker _blocker;
    private readonly AdminCaseService _adminCases;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly IMailSender _mail;
    private readonly TimeProvider _clock;

    public AdminUserService(
        IAdminUserStore store, ICaseStore cases, IProfileStore profiles, AccountBlocker blocker, AdminCaseService adminCases, IUnitOfWork uow, IAuditLog audit,
        INotificationPublisher notify, IMailSender mail, TimeProvider clock)
    {
        _store = store;
        _cases = cases;
        _profiles = profiles;
        _blocker = blocker;
        _adminCases = adminCases;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _mail = mail;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    public async Task<AdminUserListDto> ListAsync(Guid adminId, string? search, UserStatus? status, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        await RequireAdminAsync(adminId, ct);
        var now = Now();
        var (items, total) = await _store.SearchAsync(string.IsNullOrWhiteSpace(search) ? null : search.Trim(), status, now, page, pageSize, ct);
        return new AdminUserListDto(items.Select(i => ToItem(i, now)).ToList(), page, pageSize, total);
    }

    public async Task<AdminUserDto> GetAsync(Guid adminId, Guid userId, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var now = Now();
        var row = await _store.GetRowAsync(userId, now, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        var warnings = (await _store.ListWarningsAsync(userId, ct)).Select(w => new AdminWarningDto(w.Id, w.CaseId, w.IssuedById, w.Reason, w.CreatedAt, w.ExpiresAt, w.RevokedAt, w.IsActiveAt(now))).ToList();
        var cases = (await _store.ListCasesAsync(userId, 50, ct)).Select(c => new AdminUserCaseDto(
            c.Id, c.Type, c.Status, c.Decision, c.OpenedById == userId ? nameof(CaseRole.Opener) : nameof(CaseRole.Against), c.CreatedAt)).ToList();
        var bans = await _adminCases.BanDtosAsync(await _cases.ListBanRequestsForUserAsync(userId, ct), ct);
        var events = (await _store.ListBlockEventsAsync(userId, 20, ct)).Select(ToEvent).ToList();
        return new AdminUserDto(ToItem(row, now), row.User.UiLanguage, row.User.TimeZone, row.User.EmailConfirmed, await _store.CountActiveExchangesAsync(userId, ct), warnings, cases, bans, events);
    }

    /// <summary>
    /// Заблокировать (причина 10–500 символов в аудит). Последствия те же, что у одобренного запроса на бан: статус <c>Blocked</c>, сеансы отозваны, соединения отключены, обмены
    /// прерваны (партнёру нейтральная причина), заявки истекли, письмо. Необработанные запросы на бан по пользователю закрываются как одобренные. 400 <c>user_not_active</c>
    /// (уже заблокирован или удалён), <c>staff_cannot_be_blocked</c>.
    /// </summary>
    public async Task<AdminUserDto> BlockAsync(Guid adminId, Guid userId, AdminUserActionRequest request, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var peek = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        StaffRules.RequireNotSelf(adminId, userId);
        var reason = Case.CleanText(request.Reason, 10, 500, "block_reason");
        if (!peek.IsRegular)
        {
            throw new BadRequestException("staff_cannot_be_blocked", "Служебный аккаунт блокируется отдельно: через снятие сотрудника.");
        }

        BlockOutcome outcome = null!;
        await _uow.ExecuteAsync(async token =>
        {
            var pendingBans = await _cases.FindPendingBanRequestsForUserAsync(userId, token);
            var lockIds = (await _blocker.UsersToLockAsync(userId, token)).Append(adminId).Concat(pendingBans.Where(b => b.RequestedById is not null).Select(b => b.RequestedById!.Value)).ToList();
            await _uow.LockUsersAsync(lockIds, token);

            var now = Now();
            outcome = await _blocker.ApplyAsync(adminId, userId, now, "manual", reason, null, token);
            foreach (var ban in await _cases.FindPendingBanRequestsForUserAsync(userId, token))
            {
                ban.Approve(adminId, now);
                _audit.Add(new AuditEntry(adminId, AuditActions.BanDecided, "User", userId, new { banRequestId = ban.Id, approved = true, comment = "manual_block" }));
                if (ban.RequestedById is { } requester)
                {
                    _notify.Add(requester, NotificationType.BanRequestDecided, new { banRequestId = ban.Id, userId, approved = true });
                }
            }

            await _store.SaveAsync(token);
            return 0;
        }, ct);

        await _blocker.FinishAsync(outcome, ct);
        return await GetAsync(adminId, userId, ct);
    }

    /// <summary>Разблокировать (причина в аудит): аккаунт снова <c>Active</c>, войти нужно заново, прерванные обмены не возвращаются, пользователю уходит письмо. 400 <c>user_not_blocked</c>.</summary>
    public async Task<AdminUserDto> UnblockAsync(Guid adminId, Guid userId, AdminUserActionRequest request, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        _ = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        var reason = Case.CleanText(request.Reason, 10, 500, "block_reason");

        (string? Email, string Language, string Name) letter = default;
        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([adminId, userId], token);
            var user = await _profiles.FindUserForUpdateAsync(userId, token) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
            user.Unblock();
            letter = (user.Email, user.UiLanguage, user.Name);
            _audit.Add(new AuditEntry(adminId, StaffAuditActions.UserUnblocked, "User", userId, new { reason }));
            await _store.SaveAsync(token);
            return 0;
        }, ct);

        if (letter.Email is { Length: > 0 } email)
        {
            var (subject, body) = Auth.EmailTemplates.AccountUnblocked(letter.Language, letter.Name);
            await _mail.SendAsync(email, subject, body, CancellationToken.None);
        }

        return await GetAsync(adminId, userId, ct);
    }

    /// <summary>
    /// Снять предупреждение вручную (причина в аудит): оно перестаёт считаться, пользователь получает <c>WarningRevoked</c>; автоматический запрос на бан по трём предупреждениям
    /// отклоняется, если действующих стало меньше трёх. 404 <c>warning_not_found</c>; 409 <c>warning_not_active</c> (уже снято или истекло).
    /// </summary>
    public async Task<AdminUserDto> RevokeWarningAsync(Guid adminId, Guid warningId, AdminUserActionRequest request, CancellationToken ct)
    {
        await RequireAdminAsync(adminId, ct);
        var peek = await _store.FindWarningAsync(warningId, ct) ?? throw new NotFoundException("warning_not_found", "Предупреждение не найдено.");
        var reason = Case.CleanText(request.Reason, 10, 500, "warning_revoke_reason");

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([adminId, peek.UserId], token);
            var warning = await _store.FindWarningAsync(warningId, token) ?? throw new NotFoundException("warning_not_found", "Предупреждение не найдено.");
            var now = Now();
            if (!warning.IsActiveAt(now))
            {
                throw new ConflictException("warning_not_active", "Предупреждение уже снято или истекло.");
            }

            warning.Revoke(now);
            _audit.Add(new AuditEntry(adminId, StaffAuditActions.WarningRevokedManually, "User", warning.UserId, new { warningId, caseId = warning.CaseId, reason }));
            _notify.Add(warning.UserId, NotificationType.WarningRevoked, new { caseId = warning.CaseId, manual = true });
            await _store.SaveAsync(token);

            if (await _cases.CountActiveWarningsAsync(warning.UserId, now, token) < Warning.BanThreshold)
            {
                foreach (var ban in (await _cases.FindPendingBanRequestsForUserAsync(warning.UserId, token)).Where(b => b.RequestedById is null))
                {
                    ban.Reject(adminId, now);   // запрос создала система по трём предупреждениям, их стало меньше
                    _audit.Add(new AuditEntry(adminId, AuditActions.BanDecided, "User", warning.UserId, new { banRequestId = ban.Id, approved = false, reason = "warning_revoked" }));
                }
            }

            await _store.SaveAsync(token);
            return 0;
        }, ct);

        _notify.Dispatch();
        return await GetAsync(adminId, peek.UserId, ct);
    }

    // ---------- внутреннее ----------

    private static AdminUserListItemDto ToItem(AdminUserRow row, DateTime now)
    {
        var u = row.User;
        var age = u.GetAge(DateOnly.FromDateTime(now));
        return new AdminUserListItemDto(
            u.Id, u.Name, u.Status == UserStatus.Deleted ? null : u.Email, u.Status, age is null ? "Unknown" : age < 18 ? "Minor" : "Adult", u.CreatedAt, u.LastActiveAt,
            u.RatingAvg, u.RatingCount, row.ActiveWarnings, row.OpenCases);
    }

    private static BlockEventDto ToEvent(AuditLog log)
    {
        string? reason = null, source = null;
        try
        {
            using var doc = JsonDocument.Parse(log.DetailsJson);
            reason = doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            source = doc.RootElement.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        }
        catch (JsonException)
        {
            // Записи, сделанные другим кодом, без разбираемых деталей показываем без причины.
        }

        return new BlockEventDto(log.Action, log.CreatedAt, log.ActorId, reason, source);
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
