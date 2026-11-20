using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Options;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Staff;

/// <summary>Запрос на привязку: адрес личного аккаунта, язык письма (tg, ru, en; по умолчанию язык личного аккаунта) и подтверждение действия (пароль и код 2FA).</summary>
public sealed record RequestPersonalLinkRequest(string? Email, string? Language, StepUpConfirmation? Confirmation);

public sealed record LinkedAccountDto(Guid UserId, string Name, string? Email);

public sealed record PendingLinkDto(Guid Id, string PersonalEmail, DateTime ExpiresAt);

/// <summary>Действующий запрос сотрудника на снятие привязки (ждёт решения админа).</summary>
public sealed record UnlinkRequestSummaryDto(Guid Id, string Reason, DateTime CreatedAt);

/// <summary>Состояние привязки глазами сотрудника: привязанный личный аккаунт, действующий запрос на привязку и действующий запрос на снятие (если есть). Снять привязку сам сотрудник не может.</summary>
public sealed record StaffLinkStateDto(LinkedAccountDto? Linked, PendingLinkDto? Pending, UnlinkRequestSummaryDto? UnlinkRequest);

/// <summary>Запрос на снятие привязки: <c>reason</c> 10–500 символов (зачем снять).</summary>
public sealed record RequestUnlinkRequest(string? Reason);

/// <summary>Решение по запросу на снятие: комментарий необязателен, пишется в аудит.</summary>
public sealed record DecideUnlinkRequest(string? Comment);

/// <summary>Запрос на снятие привязки глазами админа.</summary>
public sealed record UnlinkRequestDto(
    Guid Id, StaffPersonDto Staff, UserRole StaffRole, LinkedAccountDto? Personal, string Reason, UnlinkRequestStatus Status, DateTime CreatedAt, Guid? DecidedById, DateTime? DecidedAt);

public sealed record UnlinkRequestListDto(IReadOnlyList<UnlinkRequestDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>Одна привязка из истории сотрудника: личный аккаунт, когда привязан, когда и кем снят. Конфликт интересов учитывает все записи, снятые тоже.</summary>
public sealed record StaffLinkHistoryDto(LinkedAccountDto Personal, DateTime LinkedAt, DateTime? UnlinkedAt, Guid? UnlinkedById, bool IsActive);

/// <summary>Что видит владелец личного аккаунта, прежде чем подтвердить: кто просит и до какого времени действует запрос.</summary>
public sealed record LinkPreviewDto(string StaffName, UserRole StaffRole, DateTime ExpiresAt);

/// <summary>Хранилище запросов на привязку (таблица PersonalLinkRequests).</summary>
public interface IPersonalLinkStore
{
    void Add(PersonalLinkRequest request);

    /// <summary>Запрос по хешу токена; <paramref name="forUpdate"/> блокирует строку до конца транзакции.</summary>
    Task<PersonalLinkRequest?> FindByTokenHashAsync(string tokenHash, bool forUpdate, CancellationToken ct);

    /// <summary>Запросы сотрудника, которые ещё не подтверждены, не отклонены и не отменены (с отслеживанием): действующие и истёкшие.</summary>
    Task<IReadOnlyList<PersonalLinkRequest>> ListOpenByStaffAsync(Guid staffId, CancellationToken ct);

    /// <summary>Сколько запросов сотрудник отправил с указанного времени (лимит против спама на чужие почты).</summary>
    Task<int> CountRequestedSinceAsync(Guid staffId, DateTime since, CancellationToken ct);

    /// <summary>Служебный аккаунт, к которому уже привязан этот личный; null, если не привязан.</summary>
    Task<Guid?> FindStaffLinkedToAsync(Guid personalUserId, CancellationToken ct);

    /// <summary>Запись в историю привязок (при подтверждении).</summary>
    void AddLink(StaffPersonalLink link);

    /// <summary>Действующая запись истории сотрудника (с отслеживанием); null, если её нет.</summary>
    Task<StaffPersonalLink?> FindActiveLinkAsync(Guid staffId, CancellationToken ct);

    /// <summary>Вся история привязок сотрудника, новые сверху.</summary>
    Task<IReadOnlyList<StaffPersonalLink>> ListLinksAsync(Guid staffId, CancellationToken ct);

    void AddUnlinkRequest(PersonalUnlinkRequest request);

    Task<PersonalUnlinkRequest?> FindUnlinkRequestAsync(Guid id, bool tracked, CancellationToken ct);

    Task<PersonalUnlinkRequest?> FindPendingUnlinkRequestAsync(Guid staffId, bool tracked, CancellationToken ct);

    Task<(IReadOnlyList<PersonalUnlinkRequest> Items, int Total)> ListUnlinkRequestsAsync(UnlinkRequestStatus? status, int page, int pageSize, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}

/// <summary>
/// Привязка личного аккаунта к служебному (раздел 3 ТЗ, правило 3): по ней модератор не берёт обращения, где участвует его личный аккаунт или его бывшие партнёры.
/// Сотрудник просит привязку (пароль и код 2FA), владелец личного аккаунта получает письмо со ссылкой и подтверждает, войдя в личный аккаунт: так доказываются и доступ
/// к почте, и владение самим аккаунтом. Не больше одного служебного на личный и одного личного на служебный. Запрос действует 24 часа, не больше 5 запросов в час.
/// </summary>
public sealed class PersonalLinkService
{
    public const int MaxRequestsPerHour = 5;

    private readonly IPersonalLinkStore _links;
    private readonly IProfileStore _profiles;
    private readonly UserManager<User> _users;
    private readonly StepUpService _stepUp;
    private readonly ICaseStore _cases;
    private readonly ModeratorCaseReleaser _releaser;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly IMailSender _mail;
    private readonly IOptions<FrontendOptions> _frontend;
    private readonly TimeProvider _clock;

    public PersonalLinkService(
        IPersonalLinkStore links, IProfileStore profiles, UserManager<User> users, StepUpService stepUp, ICaseStore cases, ModeratorCaseReleaser releaser, IUnitOfWork uow, IAuditLog audit,
        INotificationPublisher notify, IMailSender mail, IOptions<FrontendOptions> frontend, TimeProvider clock)
    {
        _links = links;
        _profiles = profiles;
        _users = users;
        _stepUp = stepUp;
        _cases = cases;
        _releaser = releaser;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _mail = mail;
        _frontend = frontend;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    // ---------- сотрудник ----------

    public async Task<StaffLinkStateDto> GetStateAsync(Guid staffId, CancellationToken ct)
    {
        var staff = await RequireStaffAsync(staffId, ct);
        return await StateAsync(staff, ct);
    }

    /// <summary>
    /// Попросить привязку. Личный аккаунт должен быть действующим обычным аккаунтом с подтверждённой почтой и не привязанным к другому служебному (иначе 404
    /// <c>personal_account_not_found</c> или 409 <c>personal_account_already_linked</c>); у сотрудника уже есть привязка: 409 <c>staff_already_linked</c>.
    /// Прежний действующий запрос отменяется новым. Письмо уходит на почту личного аккаунта.
    /// </summary>
    public async Task<StaffLinkStateDto> RequestAsync(Guid staffId, RequestPersonalLinkRequest request, CancellationToken ct)
    {
        var staff = await RequireStaffAsync(staffId, ct);
        await _stepUp.RequireAsync(staffId, request.Confirmation, ct);

        var email = StaffInvitation.Normalize(request.Email);
        var personal = email.Length == 0 ? null : await _users.FindByEmailAsync(email);
        if (personal is not { AccountType: AccountType.Regular, Status: UserStatus.Active, EmailConfirmed: true })
        {
            throw new NotFoundException("personal_account_not_found", "Действующий личный аккаунт с таким адресом не найден.");
        }

        if (staff.LinkedUserId is not null)
        {
            throw new ConflictException("staff_already_linked", "К служебному аккаунту уже привязан личный: сначала снимите привязку.");
        }

        if (await _links.FindStaffLinkedToAsync(personal.Id, ct) is not null)
        {
            throw new ConflictException("personal_account_already_linked", "Этот личный аккаунт уже привязан к другому служебному.");
        }

        var now = Now();
        if (await _links.CountRequestedSinceAsync(staffId, now.AddHours(-1), ct) >= MaxRequestsPerHour)
        {
            throw new TooManyRequestsException("link_request_rate_limited", $"Не больше {MaxRequestsPerHour} запросов на привязку в час.");
        }

        var (token, hash) = StaffInvitationTokens.Generate();
        await _uow.ExecuteAsync(async t =>
        {
            await _uow.LockUsersAsync([staffId, personal.Id], t);
            foreach (var open in await _links.ListOpenByStaffAsync(staffId, t))
            {
                open.Cancel(now);
            }

            var created = PersonalLinkRequest.Create(staffId, personal.Id, hash, now);
            _links.Add(created);
            _audit.Add(new AuditEntry(staffId, StaffAuditActions.PersonalLinkRequested, "User", personal.Id, new { requestId = created.Id }));
            await _links.SaveAsync(t);
            return 0;
        }, ct);

        var language = SupportedLanguages.IsSupported(request.Language) ? request.Language! : SupportedLanguages.IsSupported(personal.UiLanguage) ? personal.UiLanguage : SupportedLanguages.Default;
        var (subject, body) = StaffEmailTemplates.PersonalLink(language, staff.Name, staff.Role, _frontend.Value.PersonalLinkUrl(token), now + PersonalLinkRequest.Lifetime);
        await _mail.SendAsync(personal.Email!, subject, body, CancellationToken.None);
        return await StateAsync(staff, ct);
    }

    /// <summary>Отменить действующий запрос (письмо перестаёт действовать). Нечего отменять: ничего не происходит.</summary>
    public async Task<StaffLinkStateDto> CancelRequestAsync(Guid staffId, CancellationToken ct)
    {
        var staff = await RequireStaffAsync(staffId, ct);
        var now = Now();
        foreach (var open in await _links.ListOpenByStaffAsync(staffId, ct))
        {
            open.Cancel(now);
        }

        await _links.SaveAsync(ct);
        return await StateAsync(staff, ct);
    }

    /// <summary>
    /// Попросить снять привязку. Сам сотрудник привязку снять не может (иначе можно снять привязку, взять обращение своего личного аккаунта и привязать обратно): он описывает
    /// причину (`reason`, 10–500 символов), решает админ (модераторов) или суперадмин (админов и суперадминов) с подтверждением паролем и кодом 2FA. Привязки нет: 409 `not_linked`;
    /// запрос уже ждёт решения: 409 `unlink_request_already_pending`. Админы, которые вправе решить, получают уведомление `PersonalUnlinkRequested`.
    /// </summary>
    public async Task<StaffLinkStateDto> RequestUnlinkAsync(Guid staffId, RequestUnlinkRequest request, CancellationToken ct)
    {
        var staff = await RequireStaffAsync(staffId, ct);
        var admins = await ResolversAsync(staff, ct);
        PersonalUnlinkRequest created = null!;
        await _uow.ExecuteAsync(async t =>
        {
            await _uow.LockUsersAsync([staffId], t);
            var current = await _profiles.FindUserAsync(staffId, t) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
            if (current.LinkedUserId is null)
            {
                throw new ConflictException("not_linked", "Личный аккаунт не привязан.");
            }

            if (await _links.FindPendingUnlinkRequestAsync(staffId, tracked: false, t) is not null)
            {
                throw new ConflictException("unlink_request_already_pending", "Запрос на снятие уже ждёт решения админа.");
            }

            created = PersonalUnlinkRequest.Create(staffId, request.Reason);   // короткая или длинная причина: 400
            _links.AddUnlinkRequest(created);
            _audit.Add(new AuditEntry(staffId, StaffAuditActions.PersonalUnlinkRequested, "User", staffId, new { requestId = created.Id }));
            foreach (var admin in admins)
            {
                _notify.Add(admin, NotificationType.PersonalUnlinkRequested, new { requestId = created.Id, staffId, staffName = staff.Name });
            }

            await _links.SaveAsync(t);
            return 0;
        }, ct);

        _notify.Dispatch();
        return await StateAsync(staff, ct);
    }

    // ---------- админ ----------

    /// <summary>Запросы на снятие привязки (новые сверху, фильтр по статусу): админ видит запросы модераторов, суперадмин все.</summary>
    public async Task<UnlinkRequestListDto> ListUnlinkRequestsAsync(Guid adminId, UnlinkRequestStatus? status, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        var admin = await RequireAdminAsync(adminId, ct);
        var (items, total) = await _links.ListUnlinkRequestsAsync(status, page, pageSize, ct);
        var dtos = new List<UnlinkRequestDto>();
        var people = await _profiles.FindUsersAsync(items.Select(i => i.StaffId).Distinct().ToList(), ct);
        foreach (var item in items)
        {
            if (!people.TryGetValue(item.StaffId, out var staff) || !StaffRules.CanUnlinkPersonal(admin.Role, staff.Role))
            {
                total--;   // чужие по уровню запросы не показываем (и не считаем)
                continue;
            }

            LinkedAccountDto? personal = null;
            if (staff.LinkedUserId is { } id && await _profiles.FindUserAsync(id, ct) is { } p)
            {
                personal = new LinkedAccountDto(p.Id, p.Name, p.Email);
            }

            dtos.Add(new UnlinkRequestDto(item.Id, new StaffPersonDto(staff.Id, staff.Name), staff.Role, personal, item.Reason, item.Status, item.CreatedAt, item.DecidedById, item.DecidedAt));
        }

        return new UnlinkRequestListDto(dtos, page, pageSize, Math.Max(total, dtos.Count));
    }

    /// <summary>
    /// Одобрить запрос: привязка снимается (пароль и код 2FA админа). Конфликт интересов по этому личному аккаунту и его бывшим партнёрам остаётся навсегда (история привязок).
    /// Сотруднику приходит `PersonalUnlinkDecided`.
    /// </summary>
    public async Task<UnlinkRequestDto> ApproveUnlinkAsync(Guid adminId, Guid requestId, StaffActionRequest request, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(adminId, ct);
        var peek = await _links.FindUnlinkRequestAsync(requestId, tracked: false, ct) ?? throw new NotFoundException("unlink_request_not_found", "Запрос не найден.");
        if (peek.Status != UnlinkRequestStatus.Pending)
        {
            throw new Domain.Exceptions.InvalidStateTransitionException(nameof(PersonalUnlinkRequest), peek.Status.ToString(), nameof(UnlinkRequestStatus.Approved));
        }

        var target = await RequireStaffAsync(peek.StaffId, ct);
        RequireCanUnlink(admin, target);
        await _stepUp.RequireAsync(adminId, request.Confirmation, ct);
        await UnlinkCoreAsync(admin, target.Id, requestId, ct);
        return await UnlinkRequestDtoAsync(requestId, ct);
    }

    /// <summary>Отклонить запрос: привязка остаётся, сотруднику приходит `PersonalUnlinkDecided` (`approved: false`). Подтверждение паролем не нужно: привязка не меняется.</summary>
    public async Task<UnlinkRequestDto> RejectUnlinkAsync(Guid adminId, Guid requestId, DecideUnlinkRequest? request, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(adminId, ct);
        var peek = await _links.FindUnlinkRequestAsync(requestId, tracked: false, ct) ?? throw new NotFoundException("unlink_request_not_found", "Запрос не найден.");
        var target = await RequireStaffAsync(peek.StaffId, ct);
        RequireCanUnlink(admin, target);

        await _uow.ExecuteAsync(async t =>
        {
            await _uow.LockUsersAsync([adminId, target.Id], t);
            var found = await _links.FindUnlinkRequestAsync(requestId, tracked: true, t) ?? throw new NotFoundException("unlink_request_not_found", "Запрос не найден.");
            found.Reject(adminId, Now());   // уже решён: 409 invalid_state_transition
            _audit.Add(new AuditEntry(adminId, StaffAuditActions.PersonalUnlinkRejected, "User", target.Id, new { requestId, comment = request?.Comment }));
            _notify.Add(target.Id, NotificationType.PersonalUnlinkDecided, new { requestId, approved = false });
            await _links.SaveAsync(t);
            return 0;
        }, ct);

        _notify.Dispatch();
        return await UnlinkRequestDtoAsync(requestId, ct);
    }

    /// <summary>
    /// Снять привязку сотруднику без запроса (например, при снятии модератора): админ снимает модераторам, суперадмин модераторам, админам и другим суперадминам, себе никто.
    /// Пароль и код 2FA, аудит, необработанный запрос сотрудника закрывается как одобренный. 409 `not_linked`.
    /// </summary>
    public async Task<StaffLinkStateDto> UnlinkStaffAsync(Guid adminId, Guid staffId, StaffActionRequest request, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(adminId, ct);
        var target = await RequireStaffAsync(staffId, ct);
        RequireCanUnlink(admin, target);
        await _stepUp.RequireAsync(adminId, request.Confirmation, ct);
        await UnlinkCoreAsync(admin, staffId, requestId: null, ct);
        return await StateAsync((await _profiles.FindUserAsync(staffId, ct))!, ct);
    }

    /// <summary>Вся история привязок сотрудника (личные аккаунты, когда привязаны, когда и кем сняты): ею пользуется проверка конфликта интересов.</summary>
    public async Task<IReadOnlyList<StaffLinkHistoryDto>> HistoryAsync(Guid adminId, Guid staffId, CancellationToken ct)
    {
        var admin = await RequireAdminAsync(adminId, ct);
        var target = await RequireStaffAsync(staffId, ct, allowRemoved: true);   // история нужна и после снятия сотрудника
        if (!StaffRules.CanUnlinkPersonal(admin.Role, target.Role) && adminId != staffId)
        {
            throw new ForbiddenException("staff_role_not_allowed", "У вас нет прав на этого сотрудника.");
        }

        var links = await _links.ListLinksAsync(staffId, ct);
        var people = await _profiles.FindUsersAsync(links.Select(l => l.PersonalUserId).Distinct().ToList(), ct);
        return links.Select(l => new StaffLinkHistoryDto(
            new LinkedAccountDto(l.PersonalUserId, people.TryGetValue(l.PersonalUserId, out var p) ? p.Name : "—", people.GetValueOrDefault(l.PersonalUserId)?.Email),
            l.LinkedAt, l.UnlinkedAt, l.UnlinkedById, l.IsActive)).ToList();
    }

    private async Task UnlinkCoreAsync(User admin, Guid staffId, Guid? requestId, CancellationToken ct)
    {
        await _uow.ExecuteAsync(async t =>
        {
            await UnlinkInTransactionAsync(admin, staffId, requestId, t);
            return 0;
        }, ct);

        _notify.Dispatch();
    }

    /// <summary>
    /// Снятие привязки при снятии сотрудника: внутри транзакции вызывающего (он блокирует админа, сотрудника и личный аккаунт, сохраняет и рассылает уведомления). Без запроса и
    /// без отдельного подтверждения (оно уже пройдено для снятия). Нет привязки — ничего не делает.
    /// </summary>
    /// <returns>true, если привязка снята этим вызовом.</returns>
    public async Task<bool> UnlinkForRemovalAsync(User admin, Guid staffId, CancellationToken ct)
    {
        var staff = await _profiles.FindUserAsync(staffId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (staff.LinkedUserId is null)
        {
            return false;
        }

        await UnlinkInTransactionAsync(admin, staffId, null, ct);
        return true;
    }

    private async Task UnlinkInTransactionAsync(User admin, Guid staffId, Guid? requestId, CancellationToken t)
    {
        var staff = await _profiles.FindUserForUpdateAsync(staffId, t) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        var personalId = staff.LinkedUserId ?? throw new ConflictException("not_linked", "Личный аккаунт не привязан.");
        await _uow.LockUsersAsync([admin.Id, staffId, personalId], t);
        var now = Now();

        // Запись истории: если привязка создана в обход подтверждения (старые данные, разработка), историю дописываем, чтобы конфликт не потерялся.
        var link = await _links.FindActiveLinkAsync(staffId, t);
        if (link is null)
        {
            link = StaffPersonalLink.Create(staffId, personalId, now);
            _links.AddLink(link);
        }

        staff.UnlinkPersonalAccount();
        link.Unlink(admin.Id, now);

        PersonalUnlinkRequest? pending = requestId is { } id
            ? await _links.FindUnlinkRequestAsync(id, tracked: true, t)
            : await _links.FindPendingUnlinkRequestAsync(staffId, tracked: true, t);
        pending?.Approve(admin.Id, now);   // уже решён: 409 invalid_state_transition

        _audit.Add(new AuditEntry(admin.Id, StaffAuditActions.PersonalAccountUnlinked, "User", staffId, new { personalUserId = personalId, requestId = pending?.Id }));
        _notify.Add(staffId, NotificationType.PersonalUnlinkDecided, new { requestId = pending?.Id, approved = true });
        await _links.SaveAsync(t);
    }

    private static void RequireCanUnlink(User admin, User target)
    {
        StaffRules.RequireNotSelf(admin.Id, target.Id);
        if (!StaffRules.CanUnlinkPersonal(admin.Role, target.Role))
        {
            throw new ForbiddenException("staff_role_not_allowed", target.Role == UserRole.Moderator
                ? "У вас нет прав на этого сотрудника."
                : "Привязку админов и суперадминов снимает только суперадмин.");
        }
    }

    /// <summary>Админы и суперадмины, которые вправе решать запрос этого сотрудника.</summary>
    private async Task<IReadOnlyList<Guid>> ResolversAsync(User staff, CancellationToken ct)
    {
        var ids = (await _cases.ListActiveAdminIdsAsync(ct)).Where(id => id != staff.Id).ToList();
        var people = await _profiles.FindUsersAsync(ids, ct);
        return ids.Where(id => people.TryGetValue(id, out var p) && StaffRules.CanUnlinkPersonal(p.Role, staff.Role)).ToList();
    }

    private async Task<UnlinkRequestDto> UnlinkRequestDtoAsync(Guid requestId, CancellationToken ct)
    {
        var item = (await _links.FindUnlinkRequestAsync(requestId, tracked: false, ct))!;
        var staff = (await _profiles.FindUserAsync(item.StaffId, ct))!;
        LinkedAccountDto? personal = null;
        if (staff.LinkedUserId is { } id && await _profiles.FindUserAsync(id, ct) is { } p)
        {
            personal = new LinkedAccountDto(p.Id, p.Name, p.Email);
        }

        return new UnlinkRequestDto(item.Id, new StaffPersonDto(staff.Id, staff.Name), staff.Role, personal, item.Reason, item.Status, item.CreatedAt, item.DecidedById, item.DecidedAt);
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

    // ---------- владелец личного аккаунта ----------

    /// <summary>Что за запрос (для экрана подтверждения). Чужой запрос выглядит как несуществующий (404 <c>link_request_not_found</c>); недействующий: 409 с причиной.</summary>
    public async Task<LinkPreviewDto> PreviewAsync(Guid personalUserId, string? token, CancellationToken ct)
    {
        await RequireRegularAsync(personalUserId, ct);
        var request = await FindOwnAsync(personalUserId, token, forUpdate: false, ct);
        RequirePending(request);
        var staff = await _profiles.FindUserAsync(request.StaffId, ct);
        return new LinkPreviewDto(staff?.Name ?? "—", staff?.Role ?? UserRole.Moderator, request.ExpiresAt);
    }

    /// <summary>
    /// Подтвердить привязку (войдя в личный аккаунт). Одна транзакция под блокировкой обоих аккаунтов: проверка, что у сотрудника ещё нет привязки, а этот личный
    /// аккаунт не привязан к другому, привязка, запись в аудит и уведомление сотруднику (<c>PersonalLinkConfirmed</c>, в нём <c>conflictingCases</c>: сколько его
    /// обращений в работе теперь с конфликтом интересов).
    /// </summary>
    public async Task ConfirmAsync(Guid personalUserId, string? token, CancellationToken ct)
    {
        await RequireRegularAsync(personalUserId, ct);
        var peek = await FindOwnAsync(personalUserId, token, forUpdate: false, ct);

        await _uow.ExecuteAsync(async t =>
        {
            await _uow.LockUsersAsync((await _releaser.UsersToLockAsync(peek.StaffId, t)).Append(personalUserId), t);   // и участники обращений, которые могут освободиться
            var request = await FindOwnAsync(personalUserId, token, forUpdate: true, t);
            RequirePending(request);

            var staff = await _profiles.FindUserForUpdateAsync(request.StaffId, t) ?? throw new NotFoundException("link_request_not_found", "Запрос не найден.");
            var personal = await _profiles.FindUserAsync(personalUserId, t) ?? throw new NotFoundException("link_request_not_found", "Запрос не найден.");
            if (staff.IsRegular || staff.Status != UserStatus.Active)
            {
                throw new ConflictException("staff_not_active", "Служебный аккаунт больше не действует.");
            }

            if (staff.LinkedUserId is not null)
            {
                throw new ConflictException("staff_already_linked", "К служебному аккаунту уже привязан другой личный.");
            }

            if (await _links.FindStaffLinkedToAsync(personalUserId, t) is not null)
            {
                throw new ConflictException("personal_account_already_linked", "Этот личный аккаунт уже привязан к другому служебному.");
            }

            var now = Now();
            staff.LinkPersonalAccount(personal);
            _links.AddLink(StaffPersonalLink.Create(staff.Id, personalUserId, now));   // история привязок: конфликт интересов учитывает и снятые
            request.Confirm(now);
            await _links.SaveAsync(t);
            var conflictUsers = await _cases.GetConflictUserIdsForStaffAsync(staff.Id, t);
            var conflicts = (await _releaser.ReleaseAsync(null, staff.Id, conflictUsers, ModeratorCaseReleaser.ConflictReason, t)).Total;   // обращения с конфликтом возвращаются в очередь сразу
            _audit.Add(new AuditEntry(personalUserId, StaffAuditActions.PersonalAccountLinked, "User", staff.Id, new { requestId = request.Id, conflictingCases = conflicts }));
            _notify.Add(staff.Id, NotificationType.PersonalLinkConfirmed, new { userId = personalUserId, userName = personal.Name, conflictingCases = conflicts });
            await _links.SaveAsync(t);
            return 0;
        }, ct);

        _notify.Dispatch();
    }

    /// <summary>Отказаться от привязки: запрос закрывается, сотруднику приходит уведомление (<c>PersonalLinkDeclined</c>).</summary>
    public async Task DeclineAsync(Guid personalUserId, string? token, CancellationToken ct)
    {
        var personal = await RequireRegularAsync(personalUserId, ct);
        await _uow.ExecuteAsync(async t =>
        {
            var request = await FindOwnAsync(personalUserId, token, forUpdate: true, t);
            RequirePending(request);
            request.Decline(Now());
            _audit.Add(new AuditEntry(personalUserId, StaffAuditActions.PersonalLinkDeclined, "User", request.StaffId, new { requestId = request.Id }));
            _notify.Add(request.StaffId, NotificationType.PersonalLinkDeclined, new { userId = personalUserId, userName = personal.Name });
            await _links.SaveAsync(t);
            return 0;
        }, ct);

        _notify.Dispatch();
    }

    // ---------- внутреннее ----------

    private async Task<StaffLinkStateDto> StateAsync(User staff, CancellationToken ct)
    {
        LinkedAccountDto? linked = null;
        if (staff.LinkedUserId is { } id && await _profiles.FindUserAsync(id, ct) is { } personal)
        {
            linked = new LinkedAccountDto(personal.Id, personal.Name, personal.Email);
        }

        var now = Now();
        var open = (await _links.ListOpenByStaffAsync(staff.Id, ct)).Where(r => r.IsPending(now)).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        PendingLinkDto? pending = null;
        if (open is not null && await _profiles.FindUserAsync(open.PersonalUserId, ct) is { } target)
        {
            pending = new PendingLinkDto(open.Id, target.Email ?? string.Empty, open.ExpiresAt);
        }

        var unlink = await _links.FindPendingUnlinkRequestAsync(staff.Id, tracked: false, ct);
        return new StaffLinkStateDto(linked, pending, unlink is null ? null : new UnlinkRequestSummaryDto(unlink.Id, unlink.Reason, unlink.CreatedAt));
    }

    private async Task<PersonalLinkRequest> FindOwnAsync(Guid personalUserId, string? token, bool forUpdate, CancellationToken ct)
    {
        var request = string.IsNullOrWhiteSpace(token) || token.Length > 128
            ? null
            : await _links.FindByTokenHashAsync(StaffInvitationTokens.Hash(token), forUpdate, ct);
        if (request is null || request.PersonalUserId != personalUserId)
        {
            throw new NotFoundException("link_request_not_found", "Запрос на привязку не найден.");   // чужой запрос выглядит как несуществующий
        }

        return request;
    }

    private void RequirePending(PersonalLinkRequest request)
    {
        switch (request.StatusAt(Now()))
        {
            case PersonalLinkStatus.Confirmed:
                throw new ConflictException("link_request_already_confirmed", "Привязка уже подтверждена.");
            case PersonalLinkStatus.Declined:
                throw new ConflictException("link_request_declined", "Вы уже отказались от этой привязки.");
            case PersonalLinkStatus.Cancelled:
                throw new ConflictException("link_request_cancelled", "Запрос отменён.");
            case PersonalLinkStatus.Expired:
                throw new ConflictException("link_request_expired", "Срок запроса (24 часа) истёк: пусть сотрудник отправит его заново.");
        }
    }

    private async Task<User> RequireStaffAsync(Guid staffId, CancellationToken ct, bool allowRemoved = false)
    {
        var user = await _profiles.FindUserAsync(staffId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.IsRegular || user.Role == UserRole.User || (!allowRemoved && user.Status != UserStatus.Active))
        {
            throw new ForbiddenException("staff_required", "Доступно только действующему служебному аккаунту.");
        }

        return user;
    }

    private async Task<User> RequireRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Подтвердить привязку может только личный аккаунт.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }

        return user;
    }
}
