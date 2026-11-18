using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Common;
using SkillSwap.Application.Notifications;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Уведомления в интерфейсе (раздел 12 ТЗ). Новые приходят сразу через хаб <c>/hubs/notifications</c> (событие <c>notification</c>),
/// этот контроллер даёт историю и отметки «прочитано». Состав <c>payload</c> по типам описан в docs/BACKEND-DEV.md.
/// </summary>
[ApiController]
[Route("api/v1/notifications")]
[Authorize]
[Tags("Notifications")]
public class NotificationsController : ControllerBase
{
    private readonly NotificationService _notifications;

    public NotificationsController(NotificationService notifications)
    {
        _notifications = notifications;
    }

    /// <summary>
    /// Мои уведомления, новые сверху. <c>unreadOnly=true</c> — только непрочитанные; <c>page</c> с 1, <c>pageSize</c> до 50.
    /// В ответе также <c>unreadCount</c> — число всех непрочитанных (для значка).
    /// </summary>
    [HttpGet]
    [ProducesResponseType<NotificationListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(
        [FromQuery] bool unreadOnly = false, [FromQuery] int page = 1, [FromQuery] int pageSize = Paging.DefaultPageSize, CancellationToken ct = default) =>
        Ok(await _notifications.ListAsync(UserId(), unreadOnly, page, pageSize, ct));

    /// <summary>Только число непрочитанных (дешёвый запрос для значка). Метод сверх списка из раздела 12 ТЗ.</summary>
    [HttpGet("unread-count")]
    [ProducesResponseType<UnreadCountDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UnreadCount(CancellationToken ct) =>
        Ok(await _notifications.UnreadCountAsync(UserId(), ct));

    /// <summary>Отметить прочитанным. Повтор не ошибка. Чужое и несуществующее — 404 <c>notification_not_found</c>.</summary>
    [HttpPost("{id:guid}/read")]
    [ProducesResponseType<NotificationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct) =>
        Ok(await _notifications.MarkReadAsync(UserId(), id, ct));

    /// <summary>Отметить прочитанными все мои уведомления. В ответе сколько отмечено.</summary>
    [HttpPost("read-all")]
    [ProducesResponseType<MarkedReadDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReadAll(CancellationToken ct) =>
        Ok(await _notifications.MarkAllReadAsync(UserId(), ct));

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
