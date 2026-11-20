using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Снятие привязки личного аккаунта (раздел 3 ТЗ, правило 3): только админ (у модераторов) и суперадмин (у модераторов, админов и других суперадминов), с подтверждением паролем и кодом 2FA
/// и записью в аудит; себе никто. Проверка конфликта интересов учитывает всю историю привязок, поэтому снятие конфликт не убирает.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = Policies.Admin)]
[Tags("Personal account link")]
public class AdminPersonalLinkController : ControllerBase
{
    private readonly PersonalLinkService _links;

    public AdminPersonalLinkController(PersonalLinkService links)
    {
        _links = links;
    }

    /// <summary>Запросы сотрудников на снятие привязки (новые сверху); фильтр `status`: `Pending`, `Approved`, `Rejected`. Админ видит запросы модераторов, суперадмин все.</summary>
    [HttpGet("personal-unlink-requests")]
    [ProducesResponseType<UnlinkRequestListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] UnlinkRequestStatus? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _links.ListUnlinkRequestsAsync(AdminId(), status, page, pageSize, ct));

    /// <summary>
    /// Одобрить запрос: привязка снимается, тело `{ confirmation }` (пароль и код 2FA). Сотруднику приходит `PersonalUnlinkDecided`. 403 `staff_role_not_allowed` (админов снимает только
    /// суперадмин), `cannot_change_own_rights`; 404 `unlink_request_not_found`; 409 `invalid_state_transition` (уже решён), `not_linked`.
    /// </summary>
    [HttpPost("personal-unlink-requests/{id:guid}/approve")]
    [ProducesResponseType<UnlinkRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(Guid id, [FromBody] StaffActionRequest request, CancellationToken ct) =>
        Ok(await _links.ApproveUnlinkAsync(AdminId(), id, request, ct));

    /// <summary>Отклонить запрос: привязка остаётся, тело `{ comment }` необязательно (в аудит). Сотруднику приходит `PersonalUnlinkDecided` (`approved: false`).</summary>
    [HttpPost("personal-unlink-requests/{id:guid}/reject")]
    [ProducesResponseType<UnlinkRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] DecideUnlinkRequest? request, CancellationToken ct) =>
        Ok(await _links.RejectUnlinkAsync(AdminId(), id, request, ct));

    /// <summary>
    /// Снять привязку сотруднику без запроса (например, при снятии модератора); тело `{ confirmation }`. Необработанный запрос сотрудника закрывается как одобренный.
    /// 403 `staff_role_not_allowed`, `cannot_change_own_rights`; 409 `not_linked`.
    /// </summary>
    [HttpPost("staff/{staffId:guid}/personal-link/unlink")]
    [ProducesResponseType<StaffLinkStateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Unlink(Guid staffId, [FromBody] StaffActionRequest request, CancellationToken ct) =>
        Ok(await _links.UnlinkStaffAsync(AdminId(), staffId, request, ct));

    /// <summary>История привязок сотрудника: все личные аккаунты, когда-либо привязанные к нему (когда привязаны и когда и кем сняты). По ним считается конфликт интересов.</summary>
    [HttpGet("staff/{staffId:guid}/personal-links")]
    [ProducesResponseType<IReadOnlyList<StaffLinkHistoryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> History(Guid staffId, CancellationToken ct) => Ok(await _links.HistoryAsync(AdminId(), staffId, ct));

    private Guid AdminId() => Guid.Parse(User.FindFirstValue("sub")!);
}
