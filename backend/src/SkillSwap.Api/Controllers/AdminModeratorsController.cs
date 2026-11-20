using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Staff;

namespace SkillSwap.Api.Controllers;

/// <summary>Модераторы в панели админа (раздел 3 ТЗ): список с нагрузкой, карточка и снятие. Доступно админу и суперадмину.</summary>
[ApiController]
[Route("api/v1/admin/staff/moderators")]
[Authorize(Policy = Policies.Admin)]
[Tags("Admin moderators")]
public class AdminModeratorsController : ControllerBase
{
    private readonly ModeratorAdminService _moderators;

    public AdminModeratorsController(ModeratorAdminService moderators)
    {
        _moderators = moderators;
    }

    /// <summary>Модераторы по имени с нагрузкой (в работе, ждут ответа, на проверке апелляции, решено за 30 дней). `removed=true` — снятые, по умолчанию действующие.</summary>
    [HttpGet]
    [ProducesResponseType<ModeratorListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] bool removed = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _moderators.ListAsync(AdminId(), removed, page, pageSize, ct));

    /// <summary>Карточка модератора: те же поля, что в списке, и обращения, которые он держит сейчас. 404 `moderator_not_found`.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ModeratorDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await _moderators.GetAsync(AdminId(), id, ct));

    /// <summary>
    /// Снять модератора: тело `{ reason, confirmation }` (причина 10–500 символов в аудит, пароль и код 2FA). Аккаунт перестаёт действовать, сеансы отзываются, соединения разрываются,
    /// обращения в работе возвращаются в очередь, апелляции на повторной проверке возвращаются админам для назначения другого проверяющего, доступы к чату отзываются, привязка личного
    /// аккаунта снимается. 400 `removal_reason_too_short`/`_too_long` и ошибки подтверждения; 403 `staff_role_not_allowed`, `cannot_change_own_rights`; 404 `moderator_not_found`;
    /// 409 `not_a_moderator`, `staff_already_removed`.
    /// </summary>
    [HttpPost("{id:guid}/remove")]
    [ProducesResponseType<RemoveModeratorResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Remove(Guid id, [FromBody] RemoveModeratorRequest request, CancellationToken ct) => Ok(await _moderators.RemoveAsync(AdminId(), id, request, ct));

    /// <summary>
    /// Снять админа (только суперадмин): тело `{ reason, confirmation }` (причина 10–500 символов в аудит, пароль и код 2FA). Последствия те же, что у модератора, а апелляции, которые вёл
    /// админ, возвращаются в общую очередь админов. Себя и суперадмина снять нельзя. 400 `removal_reason_too_short`/`_too_long` и ошибки подтверждения; 403 `staff_role_not_allowed`
    /// (вы не суперадмин или цель суперадмин), `cannot_change_own_rights`; 404 `admin_not_found`; 409 `not_an_admin`, `staff_already_removed`.
    /// </summary>
    [HttpPost("~/api/v1/admin/staff/admins/{id:guid}/remove")]
    [ProducesResponseType<RemoveAdminResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveAdmin(Guid id, [FromBody] RemoveModeratorRequest request, CancellationToken ct) => Ok(await _moderators.RemoveAdminAsync(AdminId(), id, request, ct));

    private Guid AdminId() => Guid.Parse(User.FindFirstValue("sub")!);
}
