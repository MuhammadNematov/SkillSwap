using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SkillSwap.Api.Auth;
using SkillSwap.Api.RateLimiting;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Приглашения служебных аккаунтов (раздел 3 ТЗ). Управление: админ и суперадмин (модераторов приглашает и снимает админ или суперадмин, админов только суперадмин).
/// Каждое действие пригласившего подтверждается паролем и кодом 2FA (<c>confirmation: { password, code, isRecoveryCode }</c>) и пишется в аудит.
/// Приём по ссылке из письма доступен без входа.
/// </summary>
[ApiController]
[Route("api/v1")]
[Tags("Staff invitations")]
public class StaffInvitationsController : ControllerBase
{
    private readonly StaffInvitationService _invitations;

    public StaffInvitationsController(StaffInvitationService invitations)
    {
        _invitations = invitations;
    }

    /// <summary>
    /// Приглашения (новые сверху): админ видит приглашения модераторов, суперадмин все. Фильтр <c>status</c>: <c>Pending</c>, <c>Accepted</c>, <c>Revoked</c>, <c>Expired</c>.
    /// </summary>
    [HttpGet("admin/staff-invitations")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<StaffInvitationListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] StaffInvitationStatus? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _invitations.ListAsync(ActorId(), status, page, pageSize, ct));

    /// <summary>
    /// Пригласить: <c>email</c>, <c>role</c> (<c>Moderator</c> или <c>Admin</c>), <c>language</c> (tg, ru, en; язык письма), <c>confirmation</c> (пароль и код 2FA пригласившего).
    /// На адрес уходит письмо со ссылкой на 72 часа; токена в ответе нет. 201 и приглашение. 400: <c>invitation_email_invalid</c>, <c>invitation_role_invalid</c>, <c>step_up_required</c>,
    /// <c>invalid_password</c>, <c>two_factor_code_required</c>, <c>invalid_two_factor_code</c>. 403: <c>staff_role_not_allowed</c> (админов приглашает только суперадмин). 409:
    /// <c>email_already_registered</c>, <c>invitation_already_exists</c> (отзовите или отправьте заново), <c>two_factor_not_enabled</c>. 429 <c>account_locked_out</c>.
    /// </summary>
    [HttpPost("admin/staff-invitations")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<StaffInvitationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Invite([FromBody] InviteStaffRequest request, CancellationToken ct)
    {
        var created = await _invitations.InviteAsync(ActorId(), request, ct);
        return CreatedAtAction(nameof(List), null, created);
    }

    /// <summary>Отозвать (ссылка сразу перестаёт работать); тело <c>{ confirmation }</c>. Принятое — 409 <c>invalid_state_transition</c>; повтор безвреден. 404 <c>invitation_not_found</c>.</summary>
    [HttpPost("admin/staff-invitations/{id:guid}/revoke")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<StaffInvitationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Revoke(Guid id, [FromBody] StaffActionRequest request, CancellationToken ct) =>
        Ok(await _invitations.RevokeAsync(ActorId(), id, request, ct));

    /// <summary>Отправить заново (действующее или истёкшее): новый токен и новые 72 часа, прежняя ссылка не работает; тело <c>{ confirmation }</c>. Принятое и отозванное — 409.</summary>
    [HttpPost("admin/staff-invitations/{id:guid}/resend")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<StaffInvitationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resend(Guid id, [FromBody] StaffActionRequest request, CancellationToken ct) =>
        Ok(await _invitations.ResendAsync(ActorId(), id, request, ct));

    /// <summary>
    /// Что за приглашение (для формы): адрес, роль, кто пригласил, срок. Без входа. 404 <c>invitation_not_found</c>; 409 <c>invitation_expired</c>, <c>invitation_revoked</c>,
    /// <c>invitation_already_used</c>.
    /// </summary>
    [HttpGet("staff-invitations/{token}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.LoginPerIpPolicy)]
    [ProducesResponseType<InvitationPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Preview(string token, CancellationToken ct) => Ok(await _invitations.PreviewAsync(token, ct));

    /// <summary>
    /// Принять приглашение: <c>token</c> из ссылки, <c>name</c>, <c>password</c>, <c>uiLanguage</c>. Создаётся отдельный служебный аккаунт с ролью из приглашения (почта подтверждена
    /// приглашением). Ответ как у входа служебного без 2FA: <c>requires2faSetup: true</c> и <c>twoFactorToken</c>; дальше настройка 2FA (<c>POST /auth/2fa/setup</c> и
    /// <c>/auth/2fa/verify</c> с этим токеном), без неё работать нельзя. Ссылка одноразовая. 400: <c>invalid_name</c>, <c>password_required</c>, <c>weak_password</c>.
    /// 404 <c>invitation_not_found</c>; 409: <c>invitation_expired</c>, <c>invitation_revoked</c>, <c>invitation_already_used</c>, <c>email_already_registered</c>.
    /// </summary>
    [HttpPost("staff-invitations/accept")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.LoginPerIpPolicy)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Accept([FromBody] AcceptInvitationRequest request, CancellationToken ct) => Ok(await _invitations.AcceptAsync(request, ct));

    private Guid ActorId() => Guid.Parse(User.FindFirstValue("sub")!);
}
