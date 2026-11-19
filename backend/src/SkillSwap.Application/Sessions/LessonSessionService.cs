using FluentValidation;
using Microsoft.Extensions.Logging;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Files;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Sessions;

/// <summary>
/// Уроки (разделы 6.2, 6.3 и 9.3 ТЗ): участник предлагает время карточкой в чате, партнёр принимает, отклоняет или предлагает другое, урок
/// можно отменить, а после окончания подтвердить «Урок состоялся». Любое изменение идёт в транзакции под блокировкой обоих участников:
/// пересечение уроков проверяется по всем их обменам, и два одновременных подтверждения не поставят человека на два урока сразу.
/// Доступ только у участников обмена; чужой урок выглядит как несуществующий (404).
/// </summary>
public sealed class LessonSessionService
{
    /// <summary>Сколько предложений без ответа может висеть в обмене.</summary>
    public const int MaxOpenProposals = 5;

    /// <summary>За сколько минут до начала приходят напоминания: сутки и 15 минут (раздел 6.2 ТЗ).</summary>
    public static readonly int[] ReminderMinutes = [1440, 15];

    public const int MaxCalendarDays = 100;

    private readonly ILessonSessionStore _sessions;
    private readonly IExchangeStore _exchanges;
    private readonly IProfileStore _profiles;
    private readonly IMessageStore _messages;
    private readonly ChatService _chat;
    private readonly IChatBroadcaster _broadcaster;
    private readonly IUnitOfWork _uow;
    private readonly IBackgroundJobs _jobs;
    private readonly INotificationPublisher _notify;
    private readonly AvatarUrls _avatars;
    private readonly IValidator<ProposeSessionRequest> _proposeValidator;
    private readonly IValidator<RescheduleSessionRequest> _rescheduleValidator;
    private readonly TimeProvider _clock;
    private readonly ILogger<LessonSessionService> _logger;

    public LessonSessionService(
        ILessonSessionStore sessions, IExchangeStore exchanges, IProfileStore profiles, IMessageStore messages, ChatService chat,
        IChatBroadcaster broadcaster, IUnitOfWork uow, IBackgroundJobs jobs, INotificationPublisher notify, AvatarUrls avatars,
        IValidator<ProposeSessionRequest> proposeValidator, IValidator<RescheduleSessionRequest> rescheduleValidator,
        TimeProvider clock, ILogger<LessonSessionService> logger)
    {
        _sessions = sessions;
        _exchanges = exchanges;
        _profiles = profiles;
        _messages = messages;
        _chat = chat;
        _broadcaster = broadcaster;
        _uow = uow;
        _jobs = jobs;
        _notify = notify;
        _avatars = avatars;
        _proposeValidator = proposeValidator;
        _rescheduleValidator = rescheduleValidator;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    public static LessonSessionDto ToDto(LessonSession s, Guid userAId, Guid userBId) => new(
        s.Id, s.ExchangeId, s.TeacherId, s.StartAt, s.EndAt, s.DurationMin, s.Topic, s.Status, s.ProposedById, s.PreviousStartAt, s.CancelledById,
        [.. new[] { s.ConfirmedByA ? userAId : (Guid?)null, s.ConfirmedByB ? userBId : (Guid?)null }.OfType<Guid>()],
        s.UnconfirmedAt, s.Status == SessionStatus.Unconfirmed ? s.ConfirmDeadline : null, s.CreatedAt, s.NoShowUserId, s.LateJoinMinutes);

    // ---------- предложить время ----------

    /// <summary>
    /// Предложить время урока: в чат уходит карточка (сообщение типа <c>SessionCard</c>). Слот не раньше чем за 1 час до начала и не позже окончания
    /// обмена; у предлагающего на это время не должно быть другого подтверждённого урока.
    /// </summary>
    public async Task<LessonSessionDto> ProposeAsync(Guid userId, Guid exchangeId, ProposeSessionRequest request, CancellationToken ct)
    {
        await _proposeValidator.ValidateAndThrowAsync(request, ct);
        await RequireActiveRegularAsync(userId, ct);
        var exchange = await _chat.AuthorizeSendAsync(userId, exchangeId, ct);   // участник, обмен активен, не слишком часто

        var teacherId = request.TeacherId!.Value;
        if (teacherId != exchange.UserA.Id && teacherId != exchange.UserB.Id)
        {
            throw new BadRequestException("teacher_not_in_exchange", "Преподавать урок может только участник обмена.");
        }

        var startAt = request.StartAt!.Value.UtcDateTime;
        var (session, card) = await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([exchange.UserA.Id, exchange.UserB.Id], token);

            var fresh = await _exchanges.GetViewAsync(exchangeId, token);
            if (fresh is null || fresh.Status != ExchangeStatus.Active)
            {
                throw new ConflictException("exchange_not_active", "Обмен уже не активен: назначать уроки нельзя.");
            }

            if (await _sessions.CountProposedAsync(exchangeId, token) >= MaxOpenProposals)
            {
                throw new ConflictException("too_many_proposals", $"Одновременно может ждать ответа не больше {MaxOpenProposals} предложенных времён. Дождитесь ответа или отзовите лишние.");
            }

            var created = LessonSession.Propose(exchangeId, teacherId, userId, startAt, request.Topic, Now(), fresh.EndDate);
            if ((await _sessions.BusyUsersAsync([userId], created.StartAt, created.EndAt, null, token)).Contains(userId))
            {
                throw Overlap("you");
            }

            var message = Message.CreateSessionCard(exchangeId, userId, created.Id);
            _sessions.Add(created);
            _messages.Add(message);
            await _sessions.SaveAsync(token);
            return (created, message);
        }, ct);

        var dto = ToDto(session, exchange.UserA.Id, exchange.UserB.Id);
        ScheduleExpiry(session);
        await _chat.PublishAsync(card, _chat.ToMessageDto(card, null, dto), exchange, userId, ct);
        return dto;
    }

    // ---------- ответ, перенос, отмена ----------

    /// <summary>Принять предложенное время (только партнёр). Проверяет пересечения у обоих участников во всех их обменах.</summary>
    public async Task<LessonSessionDto> ConfirmAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var expired = false;
        var (dto, session) = await Mutate(userId, sessionId, requireActiveExchange: true, ct, async (s, exchange, token) =>
        {
            RequireAnswerer(s, userId);
            var now = Now();
            if (s.Status == SessionStatus.Proposed && now >= s.StartAt)
            {
                s.ExpireIfDue(now);   // время начала прошло, а задача ещё не успела
                expired = true;
                return;
            }

            if (s.Status == SessionStatus.Proposed)
            {
                var busy = await _sessions.BusyUsersAsync([exchange.UserA.Id, exchange.UserB.Id], s.StartAt, s.EndAt, s.Id, token);
                if (busy.Count > 0)
                {
                    throw Overlap(busy.Contains(userId) ? "you" : "partner");   // чей именно урок мешает, не раскрываем
                }
            }

            s.Confirm(userId, now);
            _notify.Add(s.ProposedById, NotificationType.SessionConfirmed, Payload(s, userId, exchange));
        });

        _notify.Dispatch();
        await PublishUpdateAsync(dto);
        if (expired)
        {
            throw new ConflictException("session_expired", "Время начала уже наступило: предложение истекло.");
        }

        ScheduleConfirmedJobs(session);
        return dto;
    }

    /// <summary>Отклонить предложенное время (только партнёр).</summary>
    public async Task<LessonSessionDto> DeclineAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var (dto, _) = await Mutate(userId, sessionId, requireActiveExchange: true, ct, (s, exchange, _) =>
        {
            RequireAnswerer(s, userId);
            s.Decline(userId);
            _notify.Add(s.ProposedById, NotificationType.SessionDeclined, Payload(s, userId, exchange));
            return Task.CompletedTask;
        });

        _notify.Dispatch();
        await PublishUpdateAsync(dto);
        return dto;
    }

    /// <summary>
    /// Перенести запланированный урок: новое время ждёт подтверждения партнёра (Scheduled → Proposed), старое освобождается. Меньше чем за
    /// 2 часа до начала нельзя: только отмена (409 <c>reschedule_too_late</c>).
    /// </summary>
    public async Task<LessonSessionDto> RescheduleAsync(Guid userId, Guid sessionId, RescheduleSessionRequest request, CancellationToken ct)
    {
        await _rescheduleValidator.ValidateAndThrowAsync(request, ct);
        await RequireActiveRegularAsync(userId, ct);
        var newStart = request.StartAt!.Value.UtcDateTime;

        var (dto, session) = await Mutate(userId, sessionId, requireActiveExchange: true, ct, (s, exchange, _) =>
        {
            var now = Now();
            if (s.Status == SessionStatus.Scheduled && s.StartAt - now < LessonSession.FreeChangeWindow)
            {
                throw new ConflictException("reschedule_too_late", "Перенести урок можно не позднее чем за 2 часа до начала; позже его можно только отменить.");
            }

            s.Reschedule(userId, newStart, now, exchange.EndDate);
            _notify.Add(PartnerOf(exchange, userId), NotificationType.SessionProposed, Payload(s, userId, exchange));
            return Task.CompletedTask;
        });

        _notify.Dispatch();
        await PublishUpdateAsync(dto);
        ScheduleExpiry(session);
        return dto;
    }

    /// <summary>
    /// Отмена. Автор отзывает своё предложение; запланированный урок любой участник отменяет не позднее чем за 2 часа до начала без последствий
    /// (<c>Cancelled</c>), позже — <c>LateCancelled</c> (учитывается модератором). После начала нельзя (409 <c>session_already_started</c>).
    /// </summary>
    public async Task<LessonSessionDto> CancelAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var (dto, _) = await Mutate(userId, sessionId, requireActiveExchange: true, ct, (s, exchange, _) =>
        {
            if (s.Status == SessionStatus.Proposed && s.ProposedById != userId)
            {
                throw new ForbiddenException("not_session_proposer", "Отозвать предложение может только его автор; чужое можно отклонить.");
            }

            if (s.Status == SessionStatus.Scheduled && Now() >= s.StartAt)
            {
                throw new ConflictException("session_already_started", "Урок уже начался: отменить его нельзя.");
            }

            s.Cancel(userId, Now());
            var payload = Payload(s, userId, exchange, late: s.Status == SessionStatus.LateCancelled);
            _notify.Add(PartnerOf(exchange, userId), NotificationType.SessionCancelled, payload);
            return Task.CompletedTask;
        });

        _notify.Dispatch();
        await PublishUpdateAsync(dto);
        return dto;
    }

    /// <summary>
    /// «Урок состоялся»: пока видео нет, после планового окончания урок неподтверждённый; когда нажали оба в течение 24 часов, он засчитан
    /// (Completed), иначе не засчитан (NotCounted). Повторное нажатие не ошибка. Работает и когда обмен уже закрыт.
    /// </summary>
    public async Task<LessonSessionDto> ConfirmManualAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var (dto, _) = await Mutate(userId, sessionId, requireActiveExchange: false, ct, (s, exchange, _) =>
        {
            var now = Now();
            if (s.Status == SessionStatus.Unconfirmed && now > s.ConfirmDeadline)
            {
                throw new ConflictException("confirm_window_closed", "Срок подтверждения (24 часа) истёк.");
            }

            if (s.ConfirmManually(userId, userId == exchange.UserA.Id, now))
            {
                _notify.Add(exchange.UserA.Id, NotificationType.SessionCompleted, Payload(s, exchange.UserB.Id, exchange));
                _notify.Add(exchange.UserB.Id, NotificationType.SessionCompleted, Payload(s, exchange.UserA.Id, exchange));
            }

            return Task.CompletedTask;
        });

        _notify.Dispatch();
        await PublishUpdateAsync(dto);
        return dto;
    }

    // ---------- чтение ----------

    public async Task<LessonSessionDto> GetAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var (session, exchange) = await LoadAsync(userId, sessionId, ct);
        return ToDto(session, exchange.UserA.Id, exchange.UserB.Id);
    }

    /// <summary>Уроки обмена (все статусы): новые по началу сверху.</summary>
    public async Task<IReadOnlyList<LessonSessionDto>> ListByExchangeAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var exchange = await _exchanges.GetViewAsync(exchangeId, ct);
        if (exchange is null || (exchange.UserA.Id != userId && exchange.UserB.Id != userId))
        {
            throw new NotFoundException("exchange_not_found", "Обмен не найден.");
        }

        return (await _sessions.ListByExchangeAsync(exchangeId, ct)).Select(s => ToDto(s, exchange.UserA.Id, exchange.UserB.Id)).ToList();
    }

    /// <summary>
    /// Календарь: мои уроки во всех обменах в промежутке <c>[from, to)</c> (UTC; без параметров — ближайшие 30 дней), по возрастанию начала.
    /// Без <c>includeClosed</c> отклонённые, истёкшие и отменённые уроки не показываются. Промежуток не больше 100 дней.
    /// </summary>
    public async Task<IReadOnlyList<CalendarSessionDto>> CalendarAsync(Guid userId, DateTimeOffset? from, DateTimeOffset? to, bool includeClosed, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var start = from?.UtcDateTime ?? Now();
        var end = to?.UtcDateTime ?? start.AddDays(30);
        if (end <= start || end - start > TimeSpan.FromDays(MaxCalendarDays))
        {
            throw new BadRequestException("invalid_range", $"Промежуток: from меньше to, не больше {MaxCalendarDays} дней.");
        }

        var rows = await _sessions.ListForUserAsync(userId, start, end, includeClosed, ct);
        var exchanges = await _exchanges.GetViewsAsync(rows.Select(r => r.Session.ExchangeId).Distinct().ToList(), ct);
        var partners = rows.Select(r => exchanges[r.Session.ExchangeId]).Select(e => e.UserA.Id == userId ? e.UserB : e.UserA).DistinctBy(u => u.Id).ToList();
        var urls = await _avatars.ForViewerAsync(userId, partners.Select(p => (p.Id, p.AvatarUrl)), ct);

        return rows.Select(r =>
        {
            var e = exchanges[r.Session.ExchangeId];
            var partner = e.UserA.Id == userId ? e.UserB : e.UserA;
            return new CalendarSessionDto(ToDto(r.Session, e.UserA.Id, e.UserB.Id), partner with { AvatarUrl = urls.GetValueOrDefault(partner.Id) });
        }).ToList();
    }

    // ---------- вызывается обменами ----------

    /// <summary>
    /// Обмен прерван, заморожен или завершён: открытые уроки отменяются без виновного. Изменения сохраняет вызывающий в своей транзакции;
    /// после её фиксации он передаёт результат в <see cref="PublishUpdatesAsync"/>, чтобы карточки в чате обновились.
    /// </summary>
    public async Task<IReadOnlyList<LessonSessionDto>> CancelOpenAsync(Guid exchangeId, Guid userAId, Guid userBId, CancellationToken ct)
    {
        var result = new List<LessonSessionDto>();
        foreach (var session in await _sessions.FindOpenForExchangeAsync(exchangeId, ct))
        {
            if (session.CancelByExchange())
            {
                result.Add(ToDto(session, userAId, userBId));
            }
        }

        return result;
    }

    public async Task PublishUpdatesAsync(IEnumerable<LessonSessionDto> updates)
    {
        foreach (var update in updates)
        {
            await PublishUpdateAsync(update);
        }
    }

    // ---------- внутреннее ----------

    private async Task<(LessonSession Session, ExchangeRow Exchange)> LoadAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var session = (await _sessions.GetManyAsync([sessionId], ct)).GetValueOrDefault(sessionId);
        var exchange = session is null ? null : await _exchanges.GetViewAsync(session.ExchangeId, ct);
        if (session is null || exchange is null || (exchange.UserA.Id != userId && exchange.UserB.Id != userId))
        {
            throw new NotFoundException("session_not_found", "Урок не найден.");   // чужой урок выглядит как несуществующий
        }

        return (session, exchange);
    }

    /// <summary>
    /// Общая часть изменения: найти урок, заблокировать обоих участников, перечитать урок под блокировкой, применить изменение, сохранить.
    /// Исключение внутри изменения откатывает транзакцию. Возвращает состояние после изменения.
    /// </summary>
    private async Task<(LessonSessionDto Dto, LessonSession Session)> Mutate(
        Guid userId, Guid sessionId, bool requireActiveExchange, CancellationToken ct,
        Func<LessonSession, ExchangeRow, CancellationToken, Task> change)
    {
        var (peek, exchange) = await LoadAsync(userId, sessionId, ct);
        if (requireActiveExchange && exchange.Status != ExchangeStatus.Active)
        {
            throw new ConflictException("exchange_not_active", "Обмен не активен: уроки в нём менять нельзя.");
        }

        return await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([exchange.UserA.Id, exchange.UserB.Id], token);
            var session = await _sessions.FindAsync(peek.Id, token) ?? throw new NotFoundException("session_not_found", "Урок не найден.");
            await change(session, exchange, token);
            await _sessions.SaveAsync(token);
            return (ToDto(session, exchange.UserA.Id, exchange.UserB.Id), session);
        }, ct);
    }

    private static void RequireAnswerer(LessonSession s, Guid userId)
    {
        if (s.ProposedById == userId && s.Status == SessionStatus.Proposed)
        {
            throw new ForbiddenException("not_session_answerer", "Ответить на своё предложение нельзя: ответ даёт партнёр.");
        }
    }

    private static Guid PartnerOf(ExchangeRow exchange, Guid userId) => exchange.UserA.Id == userId ? exchange.UserB.Id : exchange.UserA.Id;

    private static ConflictException Overlap(string who) => new(
        "slot_overlap", "На это время у одного из участников уже есть другой урок.",
        new Dictionary<string, object?> { ["conflictFor"] = who });

    private static object Payload(LessonSession s, Guid actorId, ExchangeRow exchange, bool late = false)
    {
        var actor = exchange.UserA.Id == actorId ? exchange.UserA : exchange.UserB;
        return new
        {
            sessionId = s.Id, exchangeId = s.ExchangeId, startAt = s.StartAt, previousStartAt = s.PreviousStartAt,
            userId = actor.Id, userName = actor.Name, late,
        };
    }

    /// <summary>Предложение без ответа истекает в момент начала (раздел 9.3 ТЗ). Задача сверяет время и состояние, поэтому лишние запуски безвредны.</summary>
    private void ScheduleExpiry(LessonSession s)
    {
        var id = s.Id;
        var ticks = s.StartAt.Ticks;
        _jobs.Schedule<LessonSessionJobs>(j => j.ExpireProposalAsync(id, ticks), new DateTimeOffset(s.StartAt, TimeSpan.Zero));
    }

    /// <summary>Урок подтверждён: напоминания за сутки и за 15 минут (те, что ещё впереди), проверка на 15-й минуте и закрытие комнаты с итогом урока (старт + 75 минут).</summary>
    private void ScheduleConfirmedJobs(LessonSession s)
    {
        var id = s.Id;
        var ticks = s.StartAt.Ticks;
        var now = Now();
        foreach (var minutes in ReminderMinutes)
        {
            var at = s.StartAt.AddMinutes(-minutes);
            if (at > now)
            {
                var m = minutes;
                _jobs.Schedule<LessonSessionJobs>(j => j.RemindAsync(id, ticks, m), new DateTimeOffset(at, TimeSpan.Zero));
            }
        }

        _jobs.Schedule<VideoLessonJobs>(j => j.FirstCheckAsync(id, ticks), new DateTimeOffset(s.FirstCheckAt, TimeSpan.Zero));
        _jobs.Schedule<VideoLessonJobs>(j => j.CloseRoomAsync(id, ticks), new DateTimeOffset(s.RoomClosesAt, TimeSpan.Zero));
    }

    private async Task PublishUpdateAsync(LessonSessionDto dto)
    {
        try
        {
            await _broadcaster.SessionUpdatedAsync(dto, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось разослать обновление урока {SessionId}", dto.Id);   // состояние уже сохранено, клиент увидит его при загрузке
        }
    }

    private async Task RequireActiveRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Уроки доступны только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }
    }
}
