using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Sessions;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Уроки внутри обмена (разделы 6.2, 6.3 и 9.3 ТЗ): предложить время карточкой в чате, принять, отклонить, перенести, отменить, подтвердить
/// «Урок состоялся», календарь. Время во всех запросах и ответах в UTC (ISO 8601), клиент показывает его в часовом поясе пользователя.
/// Доступ только у участников обмена; чужой обмен и урок — 404. Карточка урока в чате — сообщение типа <c>SessionCard</c> с полем <c>session</c>,
/// после каждого изменения оба участника получают в хабе событие <c>sessionUpdated</c> с актуальным уроком.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[Tags("Lessons")]
public class LessonsController : ControllerBase
{
    private readonly LessonSessionService _lessons;
    private readonly VideoRoomService _video;

    public LessonsController(LessonSessionService lessons, VideoRoomService video)
    {
        _lessons = lessons;
        _video = video;
    }

    /// <summary>
    /// Предложить время урока (в чат уходит карточка). Тело: <c>startAt</c> (с часовым поясом), <c>teacherId</c> (кто проводит: один из двух участников),
    /// <c>topic</c> (необязательно, до 200 символов). Длительность 45 минут. Ограничения: слот не раньше чем за 1 час до начала (400 <c>slot_too_soon</c>)
    /// и не позже окончания обмена (400 <c>slot_after_exchange_end</c>); у предлагающего на это время не должно быть другого подтверждённого урока
    /// (409 <c>slot_overlap</c>, <c>conflictFor: you</c>); не больше 5 предложений без ответа (409 <c>too_many_proposals</c>). Обмен должен быть активным
    /// (409 <c>chat_read_only</c>); чужой — 404 <c>exchange_not_found</c>; 400 <c>teacher_not_in_exchange</c>, <c>validation_failed</c>.
    /// </summary>
    [HttpPost("exchanges/{exchangeId:guid}/sessions")]
    [ProducesResponseType<LessonSessionDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Propose(Guid exchangeId, [FromBody] ProposeSessionRequest request, CancellationToken ct)
    {
        var created = await _lessons.ProposeAsync(UserId(), exchangeId, request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>Уроки обмена (все статусы), новые по началу сверху.</summary>
    [HttpGet("exchanges/{exchangeId:guid}/sessions")]
    [ProducesResponseType<IReadOnlyList<LessonSessionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListByExchange(Guid exchangeId, CancellationToken ct) =>
        Ok(await _lessons.ListByExchangeAsync(UserId(), exchangeId, ct));

    /// <summary>Календарь: мои уроки во всех обменах, <c>from</c> включительно, <c>to</c> исключительно (UTC, ISO 8601; по умолчанию ближайшие 30 дней). Не больше 100 дней (400 <c>invalid_range</c>). <c>includeClosed=true</c> добавляет отклонённые, истёкшие и отменённые.</summary>
    [HttpGet("me/sessions")]
    [ProducesResponseType<IReadOnlyList<CalendarSessionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Calendar(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] bool includeClosed = false, CancellationToken ct = default) =>
        Ok(await _lessons.CalendarAsync(UserId(), from, to, includeClosed, ct));

    /// <summary>Один урок. Чужой и несуществующий — 404 <c>session_not_found</c>.</summary>
    [HttpGet("sessions/{id:guid}")]
    [ProducesResponseType<LessonSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(await _lessons.GetAsync(UserId(), id, ct));

    /// <summary>
    /// Принять предложенное время (только партнёр, не автор: 403 <c>not_session_answerer</c>). Proposed → Scheduled. Проверяются пересечения у обоих участников
    /// во всех их обменах под блокировкой (409 <c>slot_overlap</c>, <c>conflictFor</c>: you или partner). Если время начала уже наступило, предложение истекло
    /// (409 <c>session_expired</c>). Уже не Proposed — 409 <c>invalid_state_transition</c>.
    /// </summary>
    [HttpPost("sessions/{id:guid}/confirm")]
    [ProducesResponseType<LessonSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken ct) =>
        Ok(await _lessons.ConfirmAsync(UserId(), id, ct));

    /// <summary>Отклонить предложенное время (только партнёр, 403 <c>not_session_answerer</c>). Proposed → Declined.</summary>
    [HttpPost("sessions/{id:guid}/decline")]
    [ProducesResponseType<LessonSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decline(Guid id, CancellationToken ct) =>
        Ok(await _lessons.DeclineAsync(UserId(), id, ct));

    /// <summary>
    /// Перенос запланированного урока: тело <c>{ "startAt": … }</c>. Scheduled → Proposed с новым временем, старое освобождается, партнёр должен подтвердить
    /// (иначе в момент нового начала урок истечёт). Меньше чем за 2 часа до начала нельзя, только отмена (409 <c>reschedule_too_late</c>). Новое время
    /// подчиняется тем же правилам, что и предложение (400 <c>slot_too_soon</c>, <c>slot_after_exchange_end</c>).
    /// </summary>
    [HttpPost("sessions/{id:guid}/reschedule")]
    [ProducesResponseType<LessonSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reschedule(Guid id, [FromBody] RescheduleSessionRequest request, CancellationToken ct) =>
        Ok(await _lessons.RescheduleAsync(UserId(), id, request, ct));

    /// <summary>
    /// Отмена. Автор отзывает своё предложение (Proposed → Cancelled; чужое — 403 <c>not_session_proposer</c>, его можно отклонить). Запланированный урок любой участник
    /// отменяет не позднее чем за 2 часа до начала без последствий (<c>Cancelled</c>), позже — <c>LateCancelled</c> (учитывается модератором).
    /// После начала нельзя (409 <c>session_already_started</c>). Партнёру приходит уведомление.
    /// </summary>
    [HttpPost("sessions/{id:guid}/cancel")]
    [ProducesResponseType<LessonSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        Ok(await _lessons.CancelAsync(UserId(), id, ct));

    /// <summary>
    /// «Урок состоялся». Пока нет видеозвонков (этап 5), после планового окончания урок становится <c>Unconfirmed</c>; когда нажали оба в течение 24 часов, он
    /// <c>Completed</c> и идёт в баланс, иначе <c>NotCounted</c>. Повтор не ошибка (<c>confirmedBy</c> показывает, кто уже нажал). Не Unconfirmed — 409
    /// <c>invalid_state_transition</c>, срок вышел — 409 <c>confirm_window_closed</c>. Работает и после закрытия обмена.
    /// </summary>
    [HttpPost("sessions/{id:guid}/confirm-manual")]
    [ProducesResponseType<LessonSessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ConfirmManual(Guid id, CancellationToken ct) =>
        Ok(await _lessons.ConfirmManualAsync(UserId(), id, ct));


    /// <summary>
    /// Токен входа в видеокомнату урока (раздел 7.2 ТЗ). Только участникам обмена (чужой урок — 404 <c>session_not_found</c>), только для подтверждённого
    /// урока и только в окне: за 10 минут до начала и до начала + 75 минут. Ответ: <c>token</c>, <c>url</c> (WebSocket медиасервера), <c>roomName</c>
    /// (<c>session-{id}</c>), <c>expiresAt</c>, <c>startAt</c>, <c>plannedEndAt</c> (начало + 45 минут: на 40-й минуте фронтенд сам показывает
    /// «осталось 5 минут»), <c>roomClosesAt</c>. Не кэшируется. Ошибки 409: <c>join_window_not_open</c> (<c>opensAt</c>), <c>join_window_closed</c> (<c>closedAt</c>),
    /// <c>session_not_joinable</c> (<c>sessionStatus</c>: урок не подтверждён или уже закрыт), <c>exchange_frozen</c> (обмен заморожен), <c>exchange_not_active</c>
    /// (обмен завершён или прерван); 503 <c>livekit_not_configured</c>.
    /// </summary>
    [HttpPost("sessions/{id:guid}/join-token")]
    [ProducesResponseType<JoinTokenDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> JoinToken(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await _video.CreateJoinTokenAsync(UserId(), id, ct));
    }
    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
