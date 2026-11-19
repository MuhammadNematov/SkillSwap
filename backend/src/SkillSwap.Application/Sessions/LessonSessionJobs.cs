using FluentValidation;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Notifications;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Sessions;

public sealed class ProposeSessionRequestValidator : AbstractValidator<ProposeSessionRequest>
{
    public ProposeSessionRequestValidator()
    {
        RuleFor(x => x.StartAt).NotNull().WithErrorCode("required");
        RuleFor(x => x.TeacherId).NotNull().WithErrorCode("required").Must(id => id != Guid.Empty).WithErrorCode("required");
        RuleFor(x => x.Topic).Must(t => t is null || t.Trim().Length <= LessonSession.TopicMaxLength).WithErrorCode("too_long");
    }
}

public sealed class RescheduleSessionRequestValidator : AbstractValidator<RescheduleSessionRequest>
{
    public RescheduleSessionRequestValidator()
    {
        RuleFor(x => x.StartAt).NotNull().WithErrorCode("required");
    }
}

/// <summary>
/// Фоновые задачи уроков (разделы 6.2, 6.3 и 9.3 ТЗ). Каждая получает id урока и время начала, на которое поставлена: после переноса задачи на
/// старое время ничего не делают. Каждая идемпотентна и проверяет текущее состояние под блокировкой обоих участников.
/// </summary>
public sealed class LessonSessionJobs
{
    private readonly ILessonSessionStore _sessions;
    private readonly IExchangeStore _exchanges;
    private readonly IUnitOfWork _uow;
    private readonly IBackgroundJobs _jobs;
    private readonly INotificationPublisher _notify;
    private readonly IChatBroadcaster _broadcaster;
    private readonly TimeProvider _clock;

    public LessonSessionJobs(
        ILessonSessionStore sessions, IExchangeStore exchanges, IUnitOfWork uow, IBackgroundJobs jobs, INotificationPublisher notify,
        IChatBroadcaster broadcaster, TimeProvider clock)
    {
        _sessions = sessions;
        _exchanges = exchanges;
        _uow = uow;
        _jobs = jobs;
        _notify = notify;
        _broadcaster = broadcaster;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>В момент начала предложение без ответа истекает (Proposed → Expired); предлагавшему приходит уведомление.</summary>
    public Task ExpireProposalAsync(Guid sessionId, long startAtTicks) => RunAsync(sessionId, startAtTicks, (s, exchange) =>
    {
        if (!s.ExpireIfDue(Now()))
        {
            return false;
        }

        _notify.Add(s.ProposedById, NotificationType.SessionExpired, Payload(s, exchange));
        return true;
    });

    /// <summary>Напоминание за 24 часа или за 15 минут (в интерфейсе и по email): обоим участникам, если урок всё ещё запланирован.</summary>
    public Task RemindAsync(Guid sessionId, long startAtTicks, int minutesBefore) => RunAsync(sessionId, startAtTicks, (s, exchange) =>
    {
        var already = minutesBefore == 1440 ? s.Reminder24Sent : s.Reminder15Sent;
        if (s.Status != SessionStatus.Scheduled || already || Now() >= s.StartAt)
        {
            return false;
        }

        s.MarkReminderSent(minutesBefore);
        foreach (var (user, partner) in new[] { (exchange.UserA, exchange.UserB), (exchange.UserB, exchange.UserA) })
        {
            _notify.Add(user.Id, NotificationType.SessionReminder, new
            {
                sessionId = s.Id, exchangeId = s.ExchangeId, startAt = s.StartAt, minutesBefore,
                userId = partner.Id, userName = partner.Name,
            });
        }

        return true;
    });

    /// <summary>
    /// Устарело: до видеозвонков урок после планового окончания становился неподтверждённым. Теперь итог подводит <see cref="VideoLessonJobs.CloseRoomAsync"/>.
    /// Метод оставлен, чтобы задачи, уже стоящие в очереди Hangfire, не падали: он ничего не делает.
    /// </summary>
    public Task EndAsync(Guid sessionId, long startAtTicks) => Task.CompletedTask;

    /// <summary>За 24 часа урок не подтвердили оба: Unconfirmed → NotCounted, баланс его не учитывает.</summary>
    public Task ExpireUnconfirmedAsync(Guid sessionId) => RunAsync(sessionId, null, (s, exchange) =>
    {
        if (!s.ExpireUnconfirmedIfDue(Now()))
        {
            return false;
        }

        _notify.Add(exchange.UserA.Id, NotificationType.SessionNotCounted, Payload(s, exchange));
        _notify.Add(exchange.UserB.Id, NotificationType.SessionNotCounted, Payload(s, exchange));
        return true;
    });

    private static object Payload(LessonSession s, ExchangeRow exchange) =>
        new { sessionId = s.Id, exchangeId = s.ExchangeId, startAt = s.StartAt, userId = s.ProposedById, userName = NameOf(exchange, s.ProposedById) };

    private static string NameOf(ExchangeRow exchange, Guid userId) => exchange.UserA.Id == userId ? exchange.UserA.Name : exchange.UserB.Name;

    private async Task RunAsync(Guid sessionId, long? startAtTicks, Func<LessonSession, ExchangeRow, bool> apply)
    {
        var peek = (await _sessions.GetManyAsync([sessionId], CancellationToken.None)).GetValueOrDefault(sessionId);
        if (peek is null || (startAtTicks is { } t && !peek.StartsAt(t)))
        {
            return;   // урока нет или он перенесён: задача, поставленная на старое время, ничего не делает
        }

        var exchange = await _exchanges.GetViewAsync(peek.ExchangeId, CancellationToken.None);
        if (exchange is null)
        {
            return;
        }

        LessonSessionDto? changed = null;
        await _uow.ExecuteAsync(async ct =>
        {
            await _uow.LockUsersAsync([exchange.UserA.Id, exchange.UserB.Id], ct);
            var session = await _sessions.FindAsync(sessionId, ct);
            if (session is not null && (startAtTicks is null || session.StartsAt(startAtTicks.Value)) && apply(session, exchange))
            {
                await _sessions.SaveAsync(ct);
                changed = LessonSessionService.ToDto(session, exchange.UserA.Id, exchange.UserB.Id);
            }

            return 0;
        }, CancellationToken.None);

        if (changed is not null)
        {
            _notify.Dispatch();
            try
            {
                await _broadcaster.SessionUpdatedAsync(changed, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // состояние сохранено; карточка обновится при следующей загрузке чата
            }
        }
    }
}
