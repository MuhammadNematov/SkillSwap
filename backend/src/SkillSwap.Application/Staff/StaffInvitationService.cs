using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Options;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Staff;

/// <summary>Приглашение: <c>language</c> (tg, ru, en) задаёт язык письма (по умолчанию язык пригласившего), <c>confirmation</c> — пароль и код 2FA пригласившего.</summary>
public sealed record InviteStaffRequest(string? Email, UserRole Role, string? Language, StepUpConfirmation? Confirmation);

/// <summary>Тело запросов «отозвать» и «отправить заново»: подтверждение действия и (для повторной отправки) язык письма, tg, ru или en.</summary>
public sealed record StaffActionRequest(StepUpConfirmation? Confirmation, string? Language = null);

/// <summary>Принять приглашение: токен из ссылки, имя (до 100 символов), пароль и язык интерфейса.</summary>
public sealed record AcceptInvitationRequest(string? Token, string? Name, string? Password, string? UiLanguage);

public sealed record StaffPersonDto(Guid Id, string Name);

public sealed record StaffInvitationDto(
    Guid Id,
    string Email,
    UserRole TargetRole,
    StaffInvitationStatus Status,
    StaffPersonDto InvitedBy,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? AcceptedAt,
    DateTime? RevokedAt,
    int ResendCount);

public sealed record StaffInvitationListDto(IReadOnlyList<StaffInvitationDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>Что видит приглашённый перед тем, как принять: адрес, роль, кто пригласил и до какого времени действует ссылка.</summary>
public sealed record InvitationPreviewDto(string Email, UserRole TargetRole, string InvitedByName, DateTime ExpiresAt);

/// <summary>Хранилище приглашений (таблица StaffInvitations).</summary>
public interface IStaffInvitationStore
{
    void Add(StaffInvitation invitation);

    Task<StaffInvitation?> FindAsync(Guid id, bool tracked, CancellationToken ct);

    /// <summary>Приглашение по хешу токена; <paramref name="forUpdate"/> блокирует строку до конца транзакции (два одновременных приёма одной ссылки).</summary>
    Task<StaffInvitation?> FindByTokenHashAsync(string tokenHash, bool forUpdate, CancellationToken ct);

    /// <summary>Приглашение на этот адрес, которое ещё не принято и не отозвано (действующее или истёкшее).</summary>
    Task<StaffInvitation?> FindOpenByEmailAsync(string normalizedEmail, CancellationToken ct);

    /// <summary>Действующие (не принятые, не отозванные, не истёкшие) приглашения, отправленные этим аккаунтом (с отслеживанием).</summary>
    Task<IReadOnlyList<StaffInvitation>> ListPendingByInviterAsync(Guid inviterId, DateTime now, CancellationToken ct);

    Task<(IReadOnlyList<StaffInvitation> Items, int Total)> ListAsync(IReadOnlyCollection<UserRole> roles, StaffInvitationStatus? status, DateTime now, int page, int pageSize, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}

/// <summary>Токены приглашений: случайные 256 бит, в базе только хеш (SHA-256, hex в нижнем регистре).</summary>
public static class StaffInvitationTokens
{
    public static (string Token, string Hash) Generate()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, Hash(token));
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

/// <summary>
/// Приглашения служебных аккаунтов (раздел 3 ТЗ, правила 2 и 4): создать, посмотреть, отозвать, отправить заново и принять по ссылке из письма. Модераторов приглашает админ или
/// суперадмин, админов только суперадмин; каждое действие пригласившего подтверждается паролем и кодом 2FA и пишется в аудит. Токена нет ни в ответах, ни в базе в открытом
/// виде: он есть только в письме. Приём создаёт отдельный служебный аккаунт (почта подтверждена самим приглашением), 2FA настраивается сразу после него.
/// </summary>
public sealed class StaffInvitationService
{
    private readonly IStaffInvitationStore _invitations;
    private readonly IProfileStore _profiles;
    private readonly UserManager<User> _users;
    private readonly StepUpService _stepUp;
    private readonly IUnitOfWork _uow;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly IMailSender _mail;
    private readonly ITwoFactorChallengeTokens _challenges;
    private readonly IOptions<FrontendOptions> _frontend;
    private readonly TimeProvider _clock;

    public StaffInvitationService(
        IStaffInvitationStore invitations, IProfileStore profiles, UserManager<User> users, StepUpService stepUp, IUnitOfWork uow, IAuditLog audit,
        INotificationPublisher notify, IMailSender mail, ITwoFactorChallengeTokens challenges, IOptions<FrontendOptions> frontend, TimeProvider clock)
    {
        _invitations = invitations;
        _profiles = profiles;
        _users = users;
        _stepUp = stepUp;
        _uow = uow;
        _audit = audit;
        _notify = notify;
        _mail = mail;
        _challenges = challenges;
        _frontend = frontend;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    // ---------- для админа ----------

    /// <summary>Приглашения, которыми вправе управлять эта роль (админ видит модераторские, суперадмин все), новые сверху.</summary>
    public async Task<StaffInvitationListDto> ListAsync(Guid actorId, StaffInvitationStatus? status, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        var actor = await RequireStaffAsync(actorId, ct);
        var (items, total) = await _invitations.ListAsync(StaffRules.ManageableRoles(actor.Role), status, Now(), page, pageSize, ct);
        return new StaffInvitationListDto(await ToDtosAsync(items, ct), page, pageSize, total);
    }

    public async Task<StaffInvitationDto> InviteAsync(Guid actorId, InviteStaffRequest request, CancellationToken ct)
    {
        var actor = await RequireStaffAsync(actorId, ct);
        StaffRules.RequireCanManage(actor.Role, request.Role);
        await _stepUp.RequireAsync(actorId, request.Confirmation, ct);

        var (token, hash) = StaffInvitationTokens.Generate();
        var now = Now();
        var invitation = StaffInvitation.Create(request.Email, request.Role, actorId, hash, now);   // адрес и роль: 400
        if (await _users.FindByEmailAsync(invitation.Email) is not null)
        {
            throw new ConflictException("email_already_registered", "Этот адрес уже занят: на него нельзя пригласить.");
        }

        if (await _invitations.FindOpenByEmailAsync(invitation.Email, ct) is not null)
        {
            throw new ConflictException("invitation_already_exists", "На этот адрес уже есть приглашение: отзовите его или отправьте заново.");
        }

        await _uow.ExecuteAsync(async token2 =>
        {
            _invitations.Add(invitation);
            _audit.Add(new AuditEntry(actorId, StaffAuditActions.StaffInvited, "StaffInvitation", invitation.Id, new { email = invitation.Email, role = invitation.TargetRole }));
            await _invitations.SaveAsync(token2);
            return 0;
        }, ct);

        await SendAsync(invitation, actor, token, LanguageFor(request.Language, actor));
        return (await ToDtosAsync([invitation], ct))[0];
    }

    /// <summary>Отозвать приглашение (ссылка сразу перестаёт работать). Принятое отозвать нельзя (409 <c>invalid_state_transition</c>); повторный отзыв ничего не меняет.</summary>
    public async Task<StaffInvitationDto> RevokeAsync(Guid actorId, Guid id, StaffActionRequest request, CancellationToken ct)
    {
        var actor = await RequireStaffAsync(actorId, ct);
        var peek = await _invitations.FindAsync(id, tracked: false, ct) ?? throw new NotFoundException("invitation_not_found", "Приглашение не найдено.");
        StaffRules.RequireCanManage(actor.Role, peek.TargetRole);
        await _stepUp.RequireAsync(actorId, request.Confirmation, ct);

        await _uow.ExecuteAsync(async token =>
        {
            var found = await _invitations.FindAsync(id, tracked: true, token) ?? throw new NotFoundException("invitation_not_found", "Приглашение не найдено.");
            if (found.Revoke(Now()))
            {
                _audit.Add(new AuditEntry(actorId, StaffAuditActions.StaffInvitationRevoked, "StaffInvitation", id, new { email = found.Email }));
                await _invitations.SaveAsync(token);
            }

            return 0;
        }, ct);

        return (await ToDtosAsync([(await _invitations.FindAsync(id, tracked: false, ct))!], ct))[0];
    }

    /// <summary>Отправить заново (действующее или истёкшее): новый токен, новые 72 часа, прежняя ссылка не работает. Принятое и отозванное — 409.</summary>
    public async Task<StaffInvitationDto> ResendAsync(Guid actorId, Guid id, StaffActionRequest request, CancellationToken ct)
    {
        var actor = await RequireStaffAsync(actorId, ct);
        var peek = await _invitations.FindAsync(id, tracked: false, ct) ?? throw new NotFoundException("invitation_not_found", "Приглашение не найдено.");
        StaffRules.RequireCanManage(actor.Role, peek.TargetRole);
        await _stepUp.RequireAsync(actorId, request.Confirmation, ct);

        var (token, hash) = StaffInvitationTokens.Generate();
        StaffInvitation renewed = peek;
        await _uow.ExecuteAsync(async t =>
        {
            renewed = await _invitations.FindAsync(id, tracked: true, t) ?? throw new NotFoundException("invitation_not_found", "Приглашение не найдено.");
            renewed.Renew(hash, Now());   // принятое и отозванное: 409 invalid_state_transition
            _audit.Add(new AuditEntry(actorId, StaffAuditActions.StaffInvitationResent, "StaffInvitation", id, new { email = renewed.Email, resend = renewed.ResendCount }));
            await _invitations.SaveAsync(t);
            return 0;
        }, ct);

        await SendAsync(renewed, actor, token, LanguageFor(request.Language, actor));
        return (await ToDtosAsync([renewed], ct))[0];
    }

    // ---------- для приглашённого (без входа) ----------

    /// <summary>Что за приглашение: нужно, чтобы показать форму. Неизвестная ссылка — 404, недействующая — 409 с причиной.</summary>
    public async Task<InvitationPreviewDto> PreviewAsync(string? token, CancellationToken ct)
    {
        var invitation = await FindByTokenAsync(token, forUpdate: false, ct);
        RequirePending(invitation);
        var inviter = await _profiles.FindUserAsync(invitation.InvitedById, ct);
        return new InvitationPreviewDto(invitation.Email, invitation.TargetRole, inviter?.Name ?? "—", invitation.ExpiresAt);
    }

    /// <summary>
    /// Принять приглашение: создаётся отдельный служебный аккаунт с этой почтой (подтверждена самим приглашением) и ролью из приглашения. Ответ как у входа служебного без 2FA:
    /// <c>requires2faSetup: true</c> и <c>twoFactorToken</c> для настройки (дальше <c>POST /auth/2fa/setup</c> и <c>/auth/2fa/verify</c> с этим токеном). Ссылка одноразовая.
    /// </summary>
    public async Task<LoginResponse> AcceptAsync(AcceptInvitationRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100)
        {
            throw new BadRequestException("invalid_name", "Имя: от 1 до 100 символов.");
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            throw new BadRequestException("password_required", "Задайте пароль.");
        }

        Guid userId = Guid.Empty;
        await _uow.ExecuteAsync(async token =>
        {
            var invitation = await FindByTokenAsync(request.Token, forUpdate: true, token);
            RequirePending(invitation);

            var language = SupportedLanguages.IsSupported(request.UiLanguage) ? request.UiLanguage! : SupportedLanguages.Default;
            var user = User.CreateStaff(invitation.Email, name, invitation.TargetRole);
            user.EmailConfirmed = true;
            user.UiLanguage = language;
            var created = await _users.CreateAsync(user, request.Password);
            if (!created.Succeeded)
            {
                throw MapCreateError(created);
            }

            invitation.Accept(user.Id, Now());
            _audit.Add(new AuditEntry(user.Id, StaffAuditActions.StaffInvitationAccepted, "StaffInvitation", invitation.Id, new { email = invitation.Email, role = invitation.TargetRole, invitedBy = invitation.InvitedById }));
            _notify.Add(invitation.InvitedById, NotificationType.StaffInvitationAccepted, new { invitationId = invitation.Id, userId = user.Id, userName = user.Name, role = invitation.TargetRole.ToString() });
            await _invitations.SaveAsync(token);
            userId = user.Id;
            return 0;
        }, ct);

        _notify.Dispatch();
        return LoginResponse.NeedsSetup(_challenges.Create(userId, ChallengePurpose.Setup));
    }

    // ---------- внутреннее ----------

    private async Task<StaffInvitation> FindByTokenAsync(string? token, bool forUpdate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            throw new NotFoundException("invitation_not_found", "Приглашение не найдено.");
        }

        return await _invitations.FindByTokenHashAsync(StaffInvitationTokens.Hash(token), forUpdate, ct)
            ?? throw new NotFoundException("invitation_not_found", "Приглашение не найдено.");
    }

    private void RequirePending(StaffInvitation invitation)
    {
        switch (invitation.StatusAt(Now()))
        {
            case StaffInvitationStatus.Accepted:
                throw new ConflictException("invitation_already_used", "Это приглашение уже принято.");
            case StaffInvitationStatus.Revoked:
                throw new ConflictException("invitation_revoked", "Приглашение отозвано.");
            case StaffInvitationStatus.Expired:
                throw new ConflictException("invitation_expired", "Срок приглашения (72 часа) истёк: попросите прислать его заново.");
        }
    }

    private async Task SendAsync(StaffInvitation invitation, User inviter, string token, string language)
    {
        var link = _frontend.Value.StaffInvitationUrl(token);
        var (subject, body) = StaffEmailTemplates.Invitation(language, inviter.Name, invitation.TargetRole, link, invitation.ExpiresAt);
        await _mail.SendAsync(invitation.Email, subject, body, CancellationToken.None);
    }

    private static string LanguageFor(string? requested, User inviter) =>
        SupportedLanguages.IsSupported(requested) ? requested! : SupportedLanguages.IsSupported(inviter.UiLanguage) ? inviter.UiLanguage : SupportedLanguages.Default;

    private async Task<IReadOnlyList<StaffInvitationDto>> ToDtosAsync(IReadOnlyList<StaffInvitation> items, CancellationToken ct)
    {
        var now = Now();
        var people = await _profiles.FindUsersAsync(items.Select(i => i.InvitedById).Distinct().ToList(), ct);
        return items.Select(i => new StaffInvitationDto(
            i.Id, i.Email, i.TargetRole, i.StatusAt(now), new StaffPersonDto(i.InvitedById, people.TryGetValue(i.InvitedById, out var p) ? p.Name : "—"),
            i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.RevokedAt, i.ResendCount)).ToList();
    }

    private async Task<User> RequireStaffAsync(Guid actorId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(actorId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (user.Role is not (UserRole.Admin or UserRole.SuperAdmin) || user.IsRegular || user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("admin_required", "Доступно только администратору.");
        }

        return user;
    }

    private static AppException MapCreateError(IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code is nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)))
        {
            return new ConflictException("email_already_registered", "Этот адрес уже занят.");
        }

        return new BadRequestException("weak_password", "Пароль не подходит: " + string.Join(", ", result.Errors.Select(e => e.Code)) + ".");
    }
}

/// <summary>Письмо с приглашением на tg, ru и en (таджикский текст требует вычитки носителем, docs/DEFERRED.md).</summary>
public static class StaffEmailTemplates
{
    public static (string Subject, string Body) Invitation(string language, string inviterName, UserRole role, string link, DateTime expiresAt)
    {
        var until = expiresAt.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
        return language switch
        {
            "ru" => (
                "SkillSwap: приглашение в команду",
                $"Здравствуйте!\n\n{inviterName} приглашает вас в SkillSwap на роль «{RoleRu(role)}». Откройте ссылку, задайте имя и пароль и настройте двухфакторную аутентификацию:\n{link}\n\nСсылка действует до {until} и открывается один раз."),
            "tg" => (
                "SkillSwap: даъват ба даста",
                $"Салом!\n\n{inviterName} шуморо ба SkillSwap бо нақши «{RoleTg(role)}» даъват мекунад. Истиноди зеринро кушоед, ном ва рамзро муайян кунед ва аутентификатсияи дуомилиро танзим намоед:\n{link}\n\nИстинод то {until} амал мекунад ва як маротиба кушода мешавад."),
            _ => (
                "SkillSwap: invitation to the team",
                $"Hello!\n\n{inviterName} invites you to SkillSwap as {RoleEn(role)}. Open the link, set your name and password and set up two-factor authentication:\n{link}\n\nThe link is valid until {until} and can be used once."),
        };
    }

    /// <summary>Письмо владельцу личного аккаунта: сотрудник просит привязать этот аккаунт к служебному. Что это значит и что делать, если просьба неожиданная.</summary>
    public static (string Subject, string Body) PersonalLink(string language, string staffName, UserRole role, string link, DateTime expiresAt)
    {
        var until = expiresAt.ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
        return language switch
        {
            "ru" => (
                "SkillSwap: просьба привязать личный аккаунт",
                $"Здравствуйте!\n\nСотрудник платформы {staffName} ({RoleRu(role)}) просит привязать ваш личный аккаунт к своему служебному. Это нужно, чтобы он не рассматривал обращения, где участвуете вы или ваши бывшие партнёры по обменам. Служебный аккаунт не получает доступа к вашему профилю и перепискам.\n\nЕсли вы знаете этого человека и согласны, войдите в свой аккаунт и откройте ссылку:\n{link}\n\nСсылка действует до {until}. Если вы не ждали этого письма, просто проигнорируйте его или откажитесь на странице по ссылке."),
            "tg" => (
                "SkillSwap: дархост барои пайваст кардани аккаунти шахсӣ",
                $"Салом!\n\nКормандии платформа {staffName} ({RoleTg(role)}) аз шумо мепурсад, ки аккаунти шахсии шуморо ба аккаунти хидматии ӯ пайваст кунад. Ин лозим аст, то ӯ муроҷиатҳоеро, ки шумо ё шарикони собиқи мубодилаи шумо дар онҳо иштирок мекунед, баррасӣ накунад. Аккаунти хидматӣ ба профил ва мукотибаи шумо дастрасӣ намегирад.\n\nАгар шумо ин шахсро мешиносед ва розӣ ҳастед, ба аккаунти худ ворид шавед ва истиноди зеринро кушоед:\n{link}\n\nИстинод то {until} амал мекунад. Агар ин номаро интизор набудед, онро нодида гиред ё дар саҳифаи истинод рад кунед."),
            _ => (
                "SkillSwap: request to link your personal account",
                $"Hello!\n\nA platform staff member, {staffName} ({RoleEn(role)}), asks to link your personal account to their staff account. This is so that they do not review cases that involve you or your former exchange partners. The staff account gets no access to your profile or conversations.\n\nIf you know this person and agree, sign in to your account and open the link:\n{link}\n\nThe link is valid until {until}. If you did not expect this email, ignore it or decline on the page."),
        };
    }

    private static string RoleRu(UserRole role) => role == UserRole.Admin ? "администратор" : "модератор";

    private static string RoleTg(UserRole role) => role == UserRole.Admin ? "администратор" : "модератор";

    private static string RoleEn(UserRole role) => role == UserRole.Admin ? "an administrator" : "a moderator";
}
