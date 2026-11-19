using Microsoft.Extensions.Logging;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Exchanges;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Sessions;

/// <summary>
/// Присутствие в видеокомнате из событий LiveKit (разделы 6.3 и 7.2 ТЗ). Событие уже записано в журнал (<c>LiveKitWebhookEvents</c>, один раз по id);
/// здесь по журналу комнаты заново строятся интервалы участников урока (<see cref="AttendanceTimeline"/>) и приводятся к виду таблицы
/// <c>SessionAttendances</c>. Так порядок доставки, повторы и потерянное событие не портят картину, а обработку можно безопасно повторить.
/// Первый вход подтверждённого урока переводит его в <c>InProgress</c>. Всё идёт в транзакции под блокировкой обоих участников, как и остальные
/// изменения урока.
/// </summary>
public sealed class VideoAttendanceService : ILiveKitEventProcessor
{
    private readonly ILiveKitWebhookEventStore _events;
    private readonly ILessonSessionStore _sessions;
    private readonly ISessionAttendanceStore _attendance;
    private readonly IExchangeStore _exchanges;
    private readonly IUnitOfWork _uow;
    private readonly IChatBroadcaster _broadcaster;
    private readonly TimeProvider _clock;
    private readonly ILogger<VideoAttendanceService> _logger;

    public VideoAttendanceService(
        ILiveKitWebhookEventStore events, ILessonSessionStore sessions, ISessionAttendanceStore attendance, IExchangeStore exchanges,
        IUnitOfWork uow, IChatBroadcaster broadcaster, TimeProvider clock, ILogger<VideoAttendanceService> logger)
    {
        _events = events;
        _sessions = sessions;
        _attendance = attendance;
        _exchanges = exchanges;
        _uow = uow;
        _broadcaster = broadcaster;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Учитывает событие в посещаемости и отмечает его обработанным. Уже обработанное пропускается (повторная доставка), необработанное
    /// (прошлая попытка упала) обрабатывается снова. События не про наши комнаты и не про присутствие просто отмечаются обработанными.
    /// Исключение значит «не обработано»: webhook ответит ошибкой, и LiveKit повторит доставку.
    /// </summary>
    public async Task ProcessAsync(string eventId, CancellationToken ct)
    {
        var peek = await _events.FindAsync(eventId, ct);
        if (peek is null || peek.ProcessedAt is not null)
        {
            return;
        }

        var room = peek.RoomName;
        var sessionId = VideoRoomService.TryParseRoomName(room);
        var session = sessionId is null || !AttendanceTimeline.IsPresenceEvent(peek.Type)
            ? null
            : (await _sessions.GetManyAsync([sessionId.Value], ct)).GetValueOrDefault(sessionId!.Value);
        var exchange = session is null ? null : await _exchanges.GetViewAsync(session.ExchangeId, ct);

        LessonSessionDto? started = null;
        await _uow.ExecuteAsync(async token =>
        {
            if (session is not null && exchange is not null)
            {
                await _uow.LockUsersAsync([exchange.UserA.Id, exchange.UserB.Id], token);
                started = await ApplyAsync(session.Id, room!, exchange, token);
            }
            else
            {
                _logger.LogDebug("Событие LiveKit {EventId} ({Type}) не относится к уроку: присутствие не меняется.", eventId, peek.Type);
            }

            var tracked = await _events.FindAsync(eventId, token);
            tracked?.MarkProcessed(_clock.GetUtcNow().UtcDateTime);
            await _events.SaveAsync(token);
            return 0;
        }, ct);

        if (started is not null)
        {
            try
            {
                await _broadcaster.SessionUpdatedAsync(started, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Не удалось разослать обновление урока {SessionId}", started.Id);   // состояние сохранено
            }
        }
    }

    /// <summary>Пересобирает интервалы обоих участников урока по журналу комнаты. Возвращает урок, если он перешёл в InProgress.</summary>
    private async Task<LessonSessionDto?> ApplyAsync(Guid sessionId, string room, ExchangeRow exchange, CancellationToken ct)
    {
        var session = await _sessions.FindAsync(sessionId, ct);
        if (session is null || !session.AcceptsPresence)
        {
            return null;   // урок не подтверждён, отменён или уже закрыт: присутствие не меняем (после закрытия итог уже подведён)
        }

        var journal = await _events.ListByRoomAsync(room, ct);
        var existing = await _attendance.ListForUpdateAsync(sessionId, ct);

        var anyone = false;
        foreach (var userId in new[] { exchange.UserA.Id, exchange.UserB.Id })
        {
            var derived = AttendanceTimeline.Build(journal, userId.ToString("D"));
            anyone |= derived.Count > 0;
            await SyncAsync(sessionId, userId, existing.Where(a => a.UserId == userId).ToList(), derived, ct);
        }

        if (!anyone || !session.MarkStarted())
        {
            return null;
        }

        await _sessions.SaveAsync(ct);
        return LessonSessionService.ToDto(session, exchange.UserA.Id, exchange.UserB.Id);
    }

    /// <summary>Приводит строки таблицы к восстановленным интервалам: лишние убираются, новые добавляются, у остальных обновляется время выхода.</summary>
    private async Task SyncAsync(
        Guid sessionId, Guid userId, List<SessionAttendance> rows, IReadOnlyList<AttendanceSpan> derived, CancellationToken ct)
    {
        // Три прохода с сохранением между ними: у участника не больше одного открытого интервала (уникальный индекс),
        // поэтому сначала убираем лишнее и закрываем, и только потом добавляем новое.
        foreach (var row in rows.Where(r => derived.All(d => d.JoinedAt != r.JoinedAt)))
        {
            _attendance.Remove(row);
        }

        foreach (var row in rows)
        {
            var matching = derived.Where(d => d.JoinedAt == row.JoinedAt).ToList();
            if (matching.Count == 1 && row.LeftAt != matching[0].LeftAt)
            {
                row.Resync(matching[0].LeftAt);
            }
        }

        await _attendance.SaveAsync(ct);

        foreach (var span in derived.Where(d => rows.All(r => r.JoinedAt != d.JoinedAt)))
        {
            _attendance.Add(SessionAttendance.Restore(sessionId, userId, span.JoinedAt, span.LeftAt));
        }

        await _attendance.SaveAsync(ct);
    }
}
