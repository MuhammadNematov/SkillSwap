using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Telegram;

namespace SkillSwap.Api.Controllers;

/// <summary>Уведомления в Telegram и переключатели «что присылать» по почте и в Telegram. Только обычные аккаунты (служебным — 403 <c>regular_account_required</c>).</summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
[Tags("Telegram")]
public class TelegramController : ControllerBase
{
    private readonly TelegramService _telegram;
    private readonly NotificationSettingsService _settings;

    public TelegramController(TelegramService telegram, NotificationSettingsService settings)
    {
        _telegram = telegram;
        _settings = settings;
    }

    /// <summary>Настроен ли бот (<c>enabled</c>; false — скрыть блок), подключён ли мой аккаунт.</summary>
    [HttpGet("telegram")]
    [ProducesResponseType<TelegramStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(await _telegram.StatusAsync(UserId(), ct));

    /// <summary>
    /// Начать подключение: одноразовая ссылка <c>https://t.me/бот?start=код</c> (10 минут; новая ссылка отменяет прежние). Открыть и нажать «Start»;
    /// после этого придёт событие <c>telegramStatus</c> хаба уведомлений. 503 <c>telegram_not_configured</c>.
    /// </summary>
    [HttpPost("telegram/link")]
    [ProducesResponseType<TelegramLinkDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Link(CancellationToken ct) => Ok(await _telegram.CreateLinkAsync(UserId(), ct));

    /// <summary>Отключить Telegram. Повтор не ошибка.</summary>
    [HttpDelete("telegram")]
    [ProducesResponseType<TelegramStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Unlink(CancellationToken ct) => Ok(await _telegram.UnlinkAsync(UserId(), ct));

    /// <summary>
    /// Переключатели по группам: <c>LessonReminders</c>, <c>LessonChanges</c>, <c>ExchangeRequests</c>, <c>Messages</c>, <c>Cases</c>. <c>email: null</c> — писем этой группы нет.
    /// По умолчанию всё включено.
    /// </summary>
    [HttpGet("notification-settings")]
    [ProducesResponseType<NotificationSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Settings(CancellationToken ct) => Ok(await _settings.GetAsync(UserId(), ct));

    /// <summary>
    /// Изменить переключатели: передаются только изменяемые (<c>items: [{ category, email?, telegram? }]</c>). 400 <c>invalid_notification_settings</c>.
    /// </summary>
    [HttpPut("notification-settings")]
    [ProducesResponseType<NotificationSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update([FromBody] UpdateNotificationSettingsRequest request, CancellationToken ct) =>
        Ok(await _settings.UpdateAsync(UserId(), request, ct));

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
