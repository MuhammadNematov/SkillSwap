using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Staff;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Привязка личного аккаунта к служебному (раздел 3 ТЗ, правило 3). Со стороны сотрудника (модератор, админ, суперадмин): состояние, запрос, отмена и снятие привязки.
/// Со стороны владельца личного аккаунта (обычный аккаунт, вошедший в систему): просмотр запроса из письма, подтверждение и отказ.
/// </summary>
[ApiController]
[Route("api/v1")]
[Tags("Personal account link")]
public class PersonalLinkController : ControllerBase
{
    private readonly PersonalLinkService _links;

    public PersonalLinkController(PersonalLinkService links)
    {
        _links = links;
    }

    /// <summary>Привязан ли личный аккаунт (`linked`: `userId`, `name`, `email`) и есть ли действующий запрос (`pending`: `id`, `personalEmail`, `expiresAt`).</summary>
    [HttpGet("me/staff-link")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<StaffLinkStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> State(CancellationToken ct) => Ok(await _links.GetStateAsync(UserId(), ct));

    /// <summary>
    /// Попросить привязку: `email` личного аккаунта, `language` (tg, ru, en; язык письма, по умолчанию язык личного аккаунта), `confirmation` (пароль и код 2FA). На почту личного
    /// аккаунта уходит письмо со ссылкой на 24 часа; прежний действующий запрос отменяется. Ошибки: 400 подтверждения (см. FRONTEND-ADMIN); 404 `personal_account_not_found`
    /// (нет такого действующего личного аккаунта с подтверждённой почтой); 409 `staff_already_linked`, `personal_account_already_linked`; 429 `link_request_rate_limited` (больше 5 в час).
    /// </summary>
    [HttpPost("me/staff-link/request")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<StaffLinkStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Request([FromBody] RequestPersonalLinkRequest request, CancellationToken ct) =>
        Ok(await _links.RequestAsync(UserId(), request, ct));

    /// <summary>Отменить действующий запрос (письмо перестаёт действовать). Нечего отменять: ничего не происходит.</summary>
    [HttpPost("me/staff-link/cancel")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<StaffLinkStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Cancel(CancellationToken ct) => Ok(await _links.CancelRequestAsync(UserId(), ct));

    /// <summary>Снять привязку; тело `{ confirmation }`. Привязки нет: 409 `not_linked`. После снятия конфликт интересов по личному аккаунту больше не проверяется.</summary>
    [HttpPost("me/staff-link/unlink")]
    [Authorize(Policy = Policies.Staff)]
    [ProducesResponseType<StaffLinkStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Unlink([FromBody] StaffActionRequest request, CancellationToken ct) =>
        Ok(await _links.UnlinkAsync(UserId(), request, ct));

    /// <summary>
    /// Что за запрос из письма: `staffName`, `staffRole`, `expiresAt`. Только вошедший личный аккаунт, которому он адресован (чужой или неизвестный токен: 404 `link_request_not_found`);
    /// 409 `link_request_expired`, `link_request_cancelled`, `link_request_declined`, `link_request_already_confirmed`.
    /// </summary>
    [HttpGet("staff-link-requests/{token}")]
    [Authorize]
    [ProducesResponseType<LinkPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Preview(string token, CancellationToken ct) => Ok(await _links.PreviewAsync(UserId(), token, ct));

    /// <summary>Подтвердить привязку (204). Сотруднику приходит `PersonalLinkConfirmed`. 409 `staff_already_linked`, `personal_account_already_linked`, `staff_not_active` и состояния запроса как выше.</summary>
    [HttpPost("staff-link-requests/{token}/confirm")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Confirm(string token, CancellationToken ct)
    {
        await _links.ConfirmAsync(UserId(), token, ct);
        return NoContent();
    }

    /// <summary>Отказаться от привязки (204). Сотруднику приходит `PersonalLinkDeclined`.</summary>
    [HttpPost("staff-link-requests/{token}/decline")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decline(string token, CancellationToken ct)
    {
        await _links.DeclineAsync(UserId(), token, ct);
        return NoContent();
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
