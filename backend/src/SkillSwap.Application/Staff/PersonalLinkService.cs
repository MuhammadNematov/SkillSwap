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

/// <summary>Состояние привязки глазами сотрудника: привязанный личный аккаунт и действующий запрос на привязку (если есть).</summary>
public sealed record StaffLinkStateDto(LinkedAccountDto? Linked, PendingLinkDto? Pending);

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
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly IMailSender _mail;
    private readonly IOptions<FrontendOptions> _frontend;
    private readonly TimeProvider _clock;

    public PersonalLinkService(
        IPersonalLinkStore links, IProfileStore profiles, UserManager<User> users, StepUpService stepUp, ICaseStore cases, IUnitOfWork uow, IAuditLog audit,
        INotificationPublisher notify, IMailSender mail, IOptions<FrontendOptions> frontend, TimeProvider clock)
    {
        _links = links;
        _profiles = profiles;
        _users = users;
        _stepUp = stepUp;
        _cases = cases;
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

    /// <summary>Снять привязку (пароль и код 2FA). Привязки нет: 409 <c>not_linked</c>. Конфликт интересов после этого проверяться перестаёт.</summary>
    public async Task<StaffLinkStateDto> UnlinkAsync(Guid staffId, StaffActionRequest request, CancellationToken ct)
    {
        await RequireStaffAsync(staffId, ct);
        await _stepUp.RequireAsync(staffId, request.Confirmation, ct);

        await _uow.ExecuteAsync(async t =>
        {
            var staff = await _profiles.FindUserForUpdateAsync(staffId, t) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
            var personalId = staff.LinkedUserId;
            await _uow.LockUsersAsync(personalId is { } id ? [staffId, id] : [staffId], t);
            if (!staff.UnlinkPersonalAccount())
            {
                throw new ConflictException("not_linked", "Личный аккаунт не привязан.");
            }

            _audit.Add(new AuditEntry(staffId, StaffAuditActions.PersonalAccountUnlinked, "User", personalId, null));
            await _links.SaveAsync(t);
            return 0;
        }, ct);

        return await StateAsync((await _profiles.FindUserAsync(staffId, ct))!, ct);
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
            await _uow.LockUsersAsync([peek.StaffId, personalUserId], t);
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
            request.Confirm(now);
            var conflicts = await _cases.CountActiveConflictsAsync(staff.Id, await _cases.GetConflictUserIdsAsync(personalUserId, t), t);
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

        return new StaffLinkStateDto(linked, pending);
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

    private async Task<User> RequireStaffAsync(Guid staffId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(staffId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.IsRegular || user.Role == UserRole.User || user.Status != UserStatus.Active)
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
