using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Staff;

namespace SkillSwap.Api.Controllers;

/// <summary>Вход в панель фоновых задач Hangfire (только суперадмин, раздел 3 ТЗ).</summary>
[ApiController]
[Route("api/v1/superadmin/hangfire")]
[Authorize(Policy = Policies.SuperAdmin)]
[Tags("Superadmin hangfire")]
public class SuperAdminHangfireController : ControllerBase
{
    private readonly HangfirePanelService _panel;

    public SuperAdminHangfireController(HangfirePanelService panel)
    {
        _panel = panel;
    }

    /// <summary>
    /// Получить вход в панель: тело `{ confirmation }` (пароль и код 2FA). Ответ: `url` с одноразовым билетом (живёт минуту, годится один раз): откройте его в **новой вкладке**,
    /// сервер поставит сессию на 30 минут (`sessionMinutes`), дальше панель работает по cookie. Открытие панели пишется в аудит. 409 `dashboard_unavailable` (фоновые задачи выключены),
    /// 400 ошибки подтверждения, 403 `staff_not_active`. Панель сама по себе (адрес `/hangfire`) без сессии отвечает 401.
    /// </summary>
    [HttpPost("session")]
    [ProducesResponseType<HangfirePanelDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Open([FromBody] OpenHangfirePanelRequest request, CancellationToken ct) =>
        Ok(await _panel.OpenAsync(Guid.Parse(User.FindFirstValue("sub")!), request, ct));
}
