using Microsoft.AspNetCore.Identity;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Staff;

/// <summary>
/// Подтверждение действия (раздел 3 ТЗ, правило 7): пароль и код 2FA (из приложения или резервный). Присылается в теле каждого запроса, который меняет роли
/// или системные настройки: <c>confirmation: { password, code, isRecoveryCode }</c>. Окна «подтверждено недавно» нет, подтверждение нужно на каждое действие.
/// </summary>
/// <param name="Password">Пароль служебного аккаунта.</param>
/// <param name="Code">Код 2FA: шесть цифр из приложения либо резервный код.</param>
/// <param name="IsRecoveryCode">Код резервный (сгорает после использования).</param>
public sealed record StepUpConfirmation(string? Password, string? Code, bool IsRecoveryCode = false);

/// <summary>Названия действий служебных аккаунтов в журнале аудита (этап 7).</summary>
public static class StaffAuditActions
{
    public const string StaffInvited = "StaffInvited";
    public const string StaffInvitationRevoked = "StaffInvitationRevoked";
    public const string StaffInvitationResent = "StaffInvitationResent";
    public const string StaffInvitationAccepted = "StaffInvitationAccepted";
    public const string StaffRemoved = "StaffRemoved";
    public const string PersonalAccountLinked = "PersonalAccountLinked";
    public const string PersonalAccountUnlinked = "PersonalAccountUnlinked";
    public const string PersonalLinkRequested = "PersonalLinkRequested";
    public const string PersonalLinkDeclined = "PersonalLinkDeclined";
    public const string PersonalUnlinkRequested = "PersonalUnlinkRequested";
    public const string PersonalUnlinkRejected = "PersonalUnlinkRejected";
    public const string UserBlocked = "UserBlocked";
    public const string UserUnblocked = "UserUnblocked";
    public const string WarningRevokedManually = "WarningRevokedManually";
    public const string SettingChanged = "SettingChanged";
    public const string StepUpFailed = "StepUpFailed";
    public const string HangfirePanelOpened = "HangfirePanelOpened";
    public const string SkillSuggestionApproved = "SkillSuggestionApproved";
    public const string SkillSuggestionMerged = "SkillSuggestionMerged";
    public const string SkillSuggestionRejected = "SkillSuggestionRejected";
    public const string CategoryCreated = "CategoryCreated";
    public const string CategoryUpdated = "CategoryUpdated";
    public const string CategoryDeleted = "CategoryDeleted";
    public const string SkillCreated = "SkillCreated";
    public const string SkillUpdated = "SkillUpdated";
    public const string SkillDeleted = "SkillDeleted";
}

/// <summary>
/// Проверка подтверждения действия: пароль и обязательный второй фактор. Неудачные попытки считаются так же, как при входе (блокировка на 15 минут после серии ошибок)
/// и пишутся в аудит без самих значений (причина и кто пытался). Использованный код 2FA сгорает: тем же кодом второе действие не подтвердить.
/// </summary>
public sealed class StepUpService
{
    private readonly UserManager<User> _users;
    private readonly TwoFactorService _twoFactor;
    private readonly IAuditLog _audit;

    public StepUpService(UserManager<User> users, TwoFactorService twoFactor, IAuditLog audit)
    {
        _users = users;
        _twoFactor = twoFactor;
        _audit = audit;
    }

    /// <summary>Проверяет подтверждение действующего служебного аккаунта; иначе бросает исключение и ничего не меняет.</summary>
    /// <returns>Служебный аккаунт, подтвердивший действие.</returns>
    public async Task<User> RequireAsync(Guid actorId, StepUpConfirmation? confirmation, CancellationToken ct)
    {
        var actor = await _users.FindByIdAsync(actorId.ToString());
        if (actor is null || actor.IsRegular || actor.Role == UserRole.User || actor.Status != UserStatus.Active)
        {
            throw new ForbiddenException("staff_required", "Действие доступно только действующему служебному аккаунту.");
        }

        if (confirmation is null || string.IsNullOrEmpty(confirmation.Password))
        {
            throw new BadRequestException("step_up_required", "Подтвердите действие паролем и кодом двухфакторной аутентификации.");
        }

        if (await _users.IsLockedOutAsync(actor))
        {
            await FailAsync(actor, "locked_out", ct);
            throw new TooManyRequestsException("account_locked_out", "Слишком много неудачных попыток. Попробуйте через 15 минут.");
        }

        if (!await _users.CheckPasswordAsync(actor, confirmation.Password))
        {
            await _users.AccessFailedAsync(actor);
            await FailAsync(actor, "invalid_password", ct);
            throw new BadRequestException("invalid_password", "Неверный пароль.");
        }

        if (!actor.TwoFactorEnabled)
        {
            throw new ConflictException("two_factor_not_enabled", "У служебного аккаунта не включена 2FA: войдите заново и настройте её.");
        }

        if (string.IsNullOrWhiteSpace(confirmation.Code))
        {
            throw new BadRequestException("two_factor_code_required", "Введите код двухфакторной аутентификации.");
        }

        if (!await _twoFactor.VerifySecondFactorAsync(actor, confirmation.Code, confirmation.IsRecoveryCode, ct))
        {
            await _users.AccessFailedAsync(actor);
            await FailAsync(actor, "invalid_code", ct);
            throw new BadRequestException("invalid_two_factor_code", "Неверный код.");
        }

        await _users.ResetAccessFailedCountAsync(actor);
        return actor;
    }

    private Task FailAsync(User actor, string reason, CancellationToken ct) =>
        _audit.WriteAsync(new AuditEntry(actor.Id, StaffAuditActions.StepUpFailed, "User", actor.Id, new { reason }), ct);
}
