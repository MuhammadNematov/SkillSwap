using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Stats;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Статистика (раздел 3 ТЗ): полная для админа и суперадмина, личная для модератора. Только агрегаты: без имён, адресов и идентификаторов людей и обращений.
/// Период `from` (включительно) и `to` (не включая), ISO 8601, время без пояса считается UTC; по умолчанию последние 30 дней, не длиннее 366 дней.
/// </summary>
[ApiController]
[Route("api/v1")]
[Tags("Stats")]
public class StatsController : ControllerBase
{
    private readonly StatsService _stats;

    public StatsController(StatsService stats)
    {
        _stats = stats;
    }

    /// <summary>
    /// Полная статистика платформы: пользователи, обмены, уроки (проведено, неявки), обращения (подано, решено, по типам и решениям, очередь по статусам), время реакции модераторов и просрочки.
    /// Обращения и реакция по дате подачи, остальное по дате события. 400 `invalid_period`, `period_too_long`.
    /// </summary>
    [HttpGet("admin/stats")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType<AdminStatsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Admin([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        Ok(await _stats.GetAdminStatsAsync(ActorId(), from, to, ct));

    /// <summary>Личная статистика модератора: сколько взял, решил (по решениям), заключений по апелляциям, сколько в работе сейчас, среднее время реакции. Только его работа. 400 `invalid_period`, `period_too_long`.</summary>
    [HttpGet("moderation/stats")]
    [Authorize(Policy = Policies.Moderator)]
    [ProducesResponseType<ModeratorStatsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Moderator([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        Ok(await _stats.GetModeratorStatsAsync(ActorId(), from, to, ct));

    private Guid ActorId() => Guid.Parse(User.FindFirstValue("sub")!);
}
