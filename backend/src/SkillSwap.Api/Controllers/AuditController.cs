using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Audit;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Журнал аудита (раздел 3 ТЗ): админ видит действия модераторов и свои (без адресов), суперадмин видит всё. Только чтение. Записи новые сверху.
/// Основные действия: чтение чата, выдача и отзыв доступа к чату, решения по обращениям и апелляциям, блокировки, предупреждения, приглашения и снятие сотрудников,
/// привязка личного аккаунта, изменение каталога и настроек. Полный список действий в нужной области отдаёт `/actions`.
/// </summary>
[ApiController]
[Route("api/v1")]
[Tags("Audit")]
public class AuditController : ControllerBase
{
    private readonly AuditJournalService _journal;

    public AuditController(AuditJournalService journal)
    {
        _journal = journal;
    }

    /// <summary>
    /// Журнал для админа: действия модераторов и свои. Суперадмин на этом маршруте видит всё. Фильтры: `actorId`, `action`, `targetType`, `targetId`, `from` (включительно) и `to` (не включая),
    /// ISO 8601 в UTC; страницы до 50. 400 `invalid_paging`, `invalid_period`, `invalid_filter`.
    /// </summary>
    [HttpGet("admin/audit")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<AuditPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AdminList(
        [FromQuery] Guid? actorId, [FromQuery] string? action, [FromQuery] string? targetType, [FromQuery] Guid? targetId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _journal.ListAsync(ViewerId(), new AuditQuery(actorId, action, targetType, targetId, from, to, page, pageSize), superAdminRoute: false, ct));

    /// <summary>Действия, которые встречаются в журнале, видимом админу (для фильтра на экране), по алфавиту.</summary>
    [HttpGet("admin/audit/actions")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AdminActions(CancellationToken ct) => Ok(await _journal.ListActionsAsync(ViewerId(), superAdminRoute: false, ct));

    /// <summary>Полный журнал (только суперадмин): все исполнители, действия системы без исполнителя (`actorId` пустой) и адреса. Фильтры и ошибки как у журнала админа.</summary>
    [HttpGet("superadmin/audit")]
    [Authorize(Policy = Policies.SuperAdmin)]
    [ProducesResponseType<AuditPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SuperAdminList(
        [FromQuery] Guid? actorId, [FromQuery] string? action, [FromQuery] string? targetType, [FromQuery] Guid? targetId, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _journal.ListAsync(ViewerId(), new AuditQuery(actorId, action, targetType, targetId, from, to, page, pageSize), superAdminRoute: true, ct));

    /// <summary>Все действия, которые встречаются в журнале (только суперадмин), по алфавиту.</summary>
    [HttpGet("superadmin/audit/actions")]
    [Authorize(Policy = Policies.SuperAdmin)]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SuperAdminActions(CancellationToken ct) => Ok(await _journal.ListActionsAsync(ViewerId(), superAdminRoute: true, ct));

    private Guid ViewerId() => Guid.Parse(User.FindFirstValue("sub")!);
}
