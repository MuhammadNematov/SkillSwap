using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Auth;
using SkillSwap.Infrastructure.Auth;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Активные сеансы (устройства) пользователя: список и завершение (раздел 10.1 ТЗ).
/// Завершённый сеанс больше не сможет обновить токен; его уже выданный access-токен доживает свои 15 минут.
/// </summary>
[ApiController]
[Route("api/v1/auth/sessions")]
[Authorize]
[Tags("Auth")]
public class SessionsController : ControllerBase
{
    private readonly SessionService _sessions;

    public SessionsController(SessionService sessions)
    {
        _sessions = sessions;
    }

    /// <summary>
    /// Список действующих сеансов, новые сверху. У сеанса, из которого сделан запрос, <c>isCurrent: true</c>. <c>deviceName</c> («Safari · macOS», «Chrome · Android», «Safari · iPhone»)
    /// и <c>deviceType</c> (<c>Desktop</c>, <c>Phone</c>, <c>Tablet</c>, <c>Unknown</c>) разобраны из User-Agent: только браузер, система и вид устройства, без места и модели.
    /// Если устройство не определить (не браузер), <c>deviceName</c> равен <c>null</c>: подпись «Неизвестное устройство» пишет интерфейс. Сырой User-Agent (<c>deviceInfo</c>) в интерфейсе не показывают.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SessionInfo>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(await _sessions.ListAsync(UserId(), CurrentSessionId(), ct));

    /// <summary>Завершает один сеанс. Чужой и несуществующий сеанс неотличимы: 404.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        await _sessions.RevokeAsync(UserId(), id, ct);
        return NoContent();
    }

    /// <summary>Завершает все сеансы, кроме текущего («выйти на остальных устройствах»).</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RevokeOthers(CancellationToken ct)
    {
        await _sessions.RevokeOthersAsync(UserId(), CurrentSessionId(), ct);
        return NoContent();
    }

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);

    private Guid? CurrentSessionId() =>
        Guid.TryParse(User.FindFirstValue(JwtAccessTokenGenerator.SessionClaim), out var id) ? id : null;
}
