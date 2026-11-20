using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.AdminUsers;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Пользователи в панели админа (раздел 3 ТЗ): поиск и карточка, блокировка и разблокировка, снятие предупреждения. Доступно админу и суперадмину; обычные аккаунты, служебными
/// управляют разделы про сотрудников. Причина блокировки и снятия пишется в аудит и пользователю не показывается.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = Policies.Admin)]
[Tags("Admin users")]
public class AdminUsersController : ControllerBase
{
    private readonly AdminUserService _users;

    public AdminUsersController(AdminUserService users)
    {
        _users = users;
    }

    /// <summary>Поиск пользователей: `search` по имени или почте (без учёта регистра), фильтр `status` (`Active`, `Blocked`, `Deleted`), страницы по имени.</summary>
    [HttpGet("users")]
    [ProducesResponseType<AdminUserListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] string? search = null, [FromQuery] UserStatus? status = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _users.ListAsync(AdminId(), search, status, page, pageSize, ct));

    /// <summary>Карточка пользователя: профиль, предупреждения (действующие и снятые), обращения, запросы на бан, история блокировок и разблокировок. 404 `user_not_found`.</summary>
    [HttpGet("users/{id:guid}")]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await _users.GetAsync(AdminId(), id, ct));

    /// <summary>
    /// Заблокировать: тело `{ reason }` (10–500 символов, в аудит). Те же последствия, что у одобренного запроса на бан: сеансы отозваны, соединения отключены, обмены прерваны
    /// (партнёру `ExchangeAborted`, причина `partner_unavailable`), заявки истекли, пользователю письмо. Необработанные запросы на бан по нему закрываются как одобренные.
    /// 400 `block_reason_too_short`/`_too_long`, `user_not_active`, `staff_cannot_be_blocked`; 403 `cannot_change_own_rights`; 404 `user_not_found`.
    /// </summary>
    [HttpPost("users/{id:guid}/block")]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Block(Guid id, [FromBody] AdminUserActionRequest request, CancellationToken ct) => Ok(await _users.BlockAsync(AdminId(), id, request, ct));

    /// <summary>
    /// Разблокировать: тело `{ reason }` (10–500 символов, в аудит). Аккаунт снова `Active`, войти нужно заново, прерванные обмены не возвращаются, пользователю уходит письмо.
    /// 400 `user_not_blocked`, `block_reason_too_short`/`_too_long`; 404 `user_not_found`.
    /// </summary>
    [HttpPost("users/{id:guid}/unblock")]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unblock(Guid id, [FromBody] AdminUserActionRequest request, CancellationToken ct) => Ok(await _users.UnblockAsync(AdminId(), id, request, ct));

    /// <summary>
    /// Снять предупреждение вручную: тело `{ reason }` (10–500 символов, в аудит). Пользователю приходит `WarningRevoked` (`manual: true`); автоматический запрос на бан по трём
    /// предупреждениям отклоняется, если действующих стало меньше трёх. Возвращает карточку пользователя. 404 `warning_not_found`; 409 `warning_not_active`.
    /// </summary>
    [HttpPost("warnings/{id:guid}/revoke")]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RevokeWarning(Guid id, [FromBody] AdminUserActionRequest request, CancellationToken ct) =>
        Ok(await _users.RevokeWarningAsync(AdminId(), id, request, ct));

    private Guid AdminId() => Guid.Parse(User.FindFirstValue("sub")!);
}
