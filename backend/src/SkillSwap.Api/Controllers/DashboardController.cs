using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Dashboard;

namespace SkillSwap.Api.Controllers;

/// <summary>Главная страница кабинета.</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
[Tags("Profile")]
public class DashboardController : ControllerBase
{
    private readonly DashboardService _dashboard;

    public DashboardController(DashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    /// <summary>
    /// Главная страница одним запросом (вместо пяти): <c>nextSession</c> (ближайший подтверждённый урок или <c>null</c>; <c>canJoinFrom</c> — когда откроется вход,
    /// за 10 минут до начала), <c>counters</c> (активные обмены, ожидающие заявки в обе стороны, непрочитанные сообщения и уведомления), <c>stats</c> (засчитанные
    /// уроки, минуты в роли преподавателя и ученика, завершённые обмены), <c>rating</c> (у «Новичка» <c>average</c> = null) и <c>actionsNeeded</c> — что ждёт ответа
    /// пользователя, самое срочное сверху: <c>AnswerRequest</c>, <c>AnswerSession</c>, <c>ConfirmSession</c>, <c>FinishExchange</c>, <c>AnswerExtension</c> (<c>WriteReview</c>
    /// появится с отзывами). Все даты в UTC. Только обычный аккаунт (служебному 403 <c>regular_account_required</c>).
    /// </summary>
    [HttpGet("dashboard")]
    [ProducesResponseType<DashboardDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await _dashboard.GetAsync(Guid.Parse(User.FindFirstValue("sub")!), ct));
}
