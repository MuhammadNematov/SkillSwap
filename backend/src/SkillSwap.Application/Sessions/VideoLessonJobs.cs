using Microsoft.Extensions.Logging;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Notifications;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Sessions;

/// <summary>
/// Автоматическая фиксация урока по присутствию в комнате (раздел 6.3 ТЗ). Две задачи Hangfire ставятся при подтверждении слота:
/// предварительная проверка на 15-й минуте и окончательная в момент закрытия комнаты (старт + 75 минут), с удалением комнаты через LiveKit API.
/// Задачи идемпотентны и привязаны ко времени начала (<c>startAtTicks</c>): задача, поставленная на старое время, после переноса ничего не делает.
/// Перед решением дообрабатываются события комнаты, которые записаны, но не учтены (см. <see cref="VideoAttendanceService"/>).
/// Совместное время считается от планового начала до закрытия комнаты: минуты до начала не засчитываются.
/// </summary>
public sealed class VideoLessonJobs
{
    private readonly ILessonSessionStore _sessions;
    private readonly ISessionAttendanceStore _attendance;
    private readonly IExchangeStore _exchanges;
    private readonly ILiveKitWebhookEventStore _events;
    private readonly ILiveKitEventProcessor _processor;
    private readonly ILiveKitRoomService _rooms;
    private readonly IUnitOfWork _uow;
    private readonly IBackgroundJobs _jobs;
    private readonly INotificationPublisher _notify;
    private readonly IChatBroadcaster _broadcaster;
    private readonly TimeProvider _clock;
    private readonly ILogger<VideoLessonJobs> _logger;

    public VideoLessonJobs(
        ILessonSessionStore sessions, ISessionAttendanceStore attendance, IExchangeStore exchanges, ILiveKitWebhookEventStore events,
        ILiveKitEventProcessor processor, ILiveKitRoomService rooms, IUnitOfWork uow, IBackgroundJobs jobs, INotificationPublisher notify,
        IChatBroadcaster broadcaster, TimeProvider clock, ILogger<VideoLessonJobs> logger)
    {
        _sessions = sessions;
        _attendance = attendance;
        _exchanges = exchanges;
        _events = events;
        _processor = processor;
        _rooms = rooms;
        _uow = uow;
        _jobs = jobs;
        _notify = notify;
        _broadcaster = broadcaster;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Проверка на 15-й минуте: никого нет — <c>Missed</c>, зашёл один — <c>NoShow</c> (виновник — не пришедший), оба — урок идёт дальше.
    /// «Зашёл» значит: был в комнате в промежутке от начала до 15-й минуты. Статус предварительный: опоздавший ещё может зайти.
    /// </summary>
    public async Task FirstCheckAsync(Guid sessionId, long startAtTicks)
    {
        var context = await LoadAsync(sessionId, startAtTicks);
        if (context is null)
        {
            return;
        }

        await CatchUpAsync(context.Room);
        var changed = await ApplyAsync(sessionId, startAtTicks, context.Exchange, (s, rows, exchange) =>
        {
            var a = exchange.UserA.Id;
            var b = exchange.UserB.Id;
            var joined = new[] { a, b }.Where(u => SessionAttendance.FirstPresenceIn(rows, u, s.StartAt, s.FirstCheckAt) is not null).ToList();
            if (!s.ApplyFirstCheck(Now(), joined, a, b))
            {
                return false;
            }

            NotifyAbsence(s, exchange);
            return true;
        });

        await PublishAsync(changed);
    }

    /// <summary>
    /// Закрытие комнаты (старт + 75 минут): открытые интервалы закрываются, подводится итог урока (6.3 ТЗ), комната удаляется в LiveKit.
    /// Итог: совместно ≥ 30 минут — <c>Completed</c>; оба были, но меньше — <c>Unconfirmed</c> (ручное подтверждение 24 часа);
    /// один — <c>NoShow</c>; никто — <c>Missed</c>. Удаление комнаты — после сохранения итога: сбой LiveKit не теряет итог, задача повторится.
    /// </summary>
    public async Task CloseRoomAsync(Guid sessionId, long startAtTicks)
    {
        var context = await LoadAsync(sessionId, startAtTicks);
        if (context is null)
        {
            return;
        }

        await CatchUpAsync(context.Room);
        var changed = await ApplyAsync(sessionId, startAtTicks, context.Exchange, (s, rows, exchange) =>
        {
            var now = Now();
            if (now < s.RoomClosesAt)
            {
                return false;   // задача сработала раньше времени (часы расходятся): итог подведёт следующий запуск
            }

            // Всё, что ещё открыто, заканчивается закрытием комнаты: события выхода после удаления комнаты уже не учитываются.
            var closedAny = false;
            foreach (var open in rows.Where(r => r.LeftAt is null))
            {
                open.Close(s.RoomClosesAt);
                closedAny = true;
            }

            var a = exchange.UserA.Id;
            var b = exchange.UserB.Id;
            var firstJoins = new Dictionary<Guid, DateTime>();
            foreach (var user in new[] { a, b })
            {
                if (SessionAttendance.FirstPresenceIn(rows, user, s.StartAt, s.RoomClosesAt) is { } at)
                {
                    firstJoins[user] = at;
                }
            }

            var joint = SessionAttendance.JointMinutes(rows, a, b, s.StartAt, s.RoomClosesAt);
            var before = s.Status;
            if (s.AcceptsPresence)
            {
                s.MarkRoomClosed(now);   // с этого момента NoShow и Missed окончательные (считаются в неявки обмена)
            }

            if (!s.CloseRoom(now, firstJoins, joint, a, b))
            {
                return closedAny || before != s.Status;   // закрылись интервалы: у карточки меняется participantsInRoom
            }

            NotifyOutcome(s, before, exchange);
            if (s.Status == SessionStatus.Unconfirmed)
            {
                var id = s.Id;
                _jobs.Schedule<LessonSessionJobs>(j => j.ExpireUnconfirmedAsync(id), new DateTimeOffset(s.ConfirmDeadline, TimeSpan.Zero));
            }

            return true;
        }, alwaysSave: true);

        await PublishAsync(changed);
        // Комната существует, только если урок был подтверждён (или идёт). Раньше срока (часы расходятся) и без настроенного LiveKit не трогаем.

        var hadRoom = context.Session.Status is SessionStatus.Scheduled or SessionStatus.InProgress or SessionStatus.NoShow or SessionStatus.Missed
            or SessionStatus.Completed or SessionStatus.Unconfirmed;
        if (hadRoom && Now() >= context.Session.RoomClosesAt && _rooms.IsConfigured)
        {
            await _rooms.DeleteRoomAsync(context.Room, CancellationToken.None);
        }
    }

    // ---------- внутреннее ----------

    private sealed record Context(LessonSession Session, ExchangeRow Exchange, string Room);

    private async Task<Context?> LoadAsync(Guid sessionId, long startAtTicks)
    {
        var peek = (await _sessions.GetManyAsync([sessionId], CancellationToken.None)).GetValueOrDefault(sessionId);
        if (peek is null || !peek.StartsAt(startAtTicks))
        {
            return null;   // урока нет или он перенесён: задача на прежнее время ничего не делает
        }

        var exchange = await _exchanges.GetViewAsync(peek.ExchangeId, CancellationToken.None);
        return exchange is null ? null : new Context(peek, exchange, VideoRoomService.RoomName(sessionId));
    }

    /// <summary>События комнаты, записанные, но не учтённые (LiveKit не повторил доставку), доучитываются до решения.</summary>
    private async Task CatchUpAsync(string room)
    {
        foreach (var eventId in await _events.ListUnprocessedIdsAsync(room, CancellationToken.None))
        {
            await _processor.ProcessAsync(eventId, CancellationToken.None);
        }
    }

    private async Task<LessonSessionDto?> ApplyAsync(
        Guid sessionId, long startAtTicks, ExchangeRow exchange, Func<LessonSession, IReadOnlyList<SessionAttendance>, ExchangeRow, bool> apply,
        bool alwaysSave = false)
    {
        LessonSessionDto? changed = null;
        await _uow.ExecuteAsync(async ct =>
        {
            await _uow.LockUsersAsync([exchange.UserA.Id, exchange.UserB.Id], ct);
            var session = await _sessions.FindAsync(sessionId, ct);
            if (session is null || !session.StartsAt(startAtTicks))
            {
                return 0;
            }

            var rows = await _attendance.ListForUpdateAsync(sessionId, ct);
            if (apply(session, rows, exchange))
            {
                changed = LessonSessionService.ToDto(session, exchange.UserA.Id, exchange.UserB.Id);
            }

            if (changed is not null || alwaysSave)
            {
                await _sessions.SaveAsync(ct);
                await _attendance.SaveAsync(ct);
            }

            return 0;
        }, CancellationToken.None);

        if (changed is not null)
        {
            _notify.Dispatch();
        }

        return changed;
    }

    private async Task PublishAsync(LessonSessionDto? changed)
    {
        if (changed is null)
        {
            return;
        }

        try
        {
            await _broadcaster.SessionUpdatedAsync(changed, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось разослать обновление урока {SessionId}", changed.Id);   // состояние сохранено
        }
    }

    private void NotifyAbsence(LessonSession s, ExchangeRow exchange)
    {
        if (s.Status == SessionStatus.Missed)
        {
            _notify.Add(exchange.UserA.Id, NotificationType.SessionMissed, Payload(s, exchange.UserB));
            _notify.Add(exchange.UserB.Id, NotificationType.SessionMissed, Payload(s, exchange.UserA));
        }
        else if (s.Status == SessionStatus.NoShow && s.NoShowUserId is { } culpritId)
        {
            var culprit = exchange.UserA.Id == culpritId ? exchange.UserA : exchange.UserB;
            _notify.Add(exchange.UserA.Id, NotificationType.SessionNoShow, Payload(s, culprit));
            _notify.Add(exchange.UserB.Id, NotificationType.SessionNoShow, Payload(s, culprit));
        }
    }

    /// <summary>Итог при закрытии комнаты. О «не пришёл» и «никто не пришёл» уже сообщила проверка на 15-й минуте: второй раз не уведомляем.</summary>
    private void NotifyOutcome(LessonSession s, SessionStatus before, ExchangeRow exchange)
    {
        switch (s.Status)
        {
            case SessionStatus.Completed:
                _notify.Add(exchange.UserA.Id, NotificationType.SessionCompleted, Payload(s, exchange.UserB));
                _notify.Add(exchange.UserB.Id, NotificationType.SessionCompleted, Payload(s, exchange.UserA));
                break;
            case SessionStatus.Unconfirmed:
                _notify.Add(exchange.UserA.Id, NotificationType.SessionNeedsConfirmation, Payload(s, exchange.UserB));
                _notify.Add(exchange.UserB.Id, NotificationType.SessionNeedsConfirmation, Payload(s, exchange.UserA));
                break;
            case SessionStatus.NoShow or SessionStatus.Missed when before is SessionStatus.Scheduled or SessionStatus.InProgress:
                NotifyAbsence(s, exchange);
                break;
        }
    }

    private static object Payload(LessonSession s, RequestUserDto other) => new
    {
        sessionId = s.Id, exchangeId = s.ExchangeId, startAt = s.StartAt, userId = other.Id, userName = other.Name,
        noShowUserId = s.NoShowUserId, lateJoinMinutes = s.LateJoinMinutes,
    };
}
