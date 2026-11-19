using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Урок внутри обмена (таблица Sessions, разделы 6.2, 6.3 и 9.3 ТЗ): предложенное в чате время, которое партнёр принимает, отклоняет или
/// предлагает перенести. Все переходы статуса — только методами этого класса; правила времени (час до начала, 2 часа до отмены) тоже здесь.
/// Время везде в UTC.
/// </summary>
public class LessonSession : BaseEntity
{
    public const int DurationMinutes = 45;
    public const int TopicMaxLength = 200;

    /// <summary>Слот нельзя предложить раньше чем за этот срок до начала (раздел 6.2 ТЗ).</summary>
    public static readonly TimeSpan MinLeadTime = TimeSpan.FromHours(1);

    /// <summary>Отмена и перенос без последствий — не позднее чем за этот срок до начала; позже отмена — <see cref="SessionStatus.LateCancelled"/>.</summary>
    public static readonly TimeSpan FreeChangeWindow = TimeSpan.FromHours(2);

    /// <summary>Сколько после планового окончания участники могут подтвердить урок вручную (раздел 6.3 ТЗ).</summary>
    public static readonly TimeSpan ManualConfirmWindow = TimeSpan.FromHours(24);

    /// <summary>Вход в комнату открывается за столько до начала (раздел 7.2 ТЗ).</summary>
    public static readonly TimeSpan JoinOpensBefore = TimeSpan.FromMinutes(10);

    /// <summary>Вход закрывается через столько после планового окончания (45 + 30 = старт + 75 минут); тогда же проверка итога и удаление комнаты.</summary>
    public static readonly TimeSpan JoinClosesAfterEnd = TimeSpan.FromMinutes(30);

    /// <summary>Предварительная проверка («пришли ли оба») — через столько после начала (раздел 6.3 ТЗ).</summary>
    public static readonly TimeSpan FirstCheckAfterStart = TimeSpan.FromMinutes(15);

    /// <summary>Сколько минут оба должны провести в комнате одновременно, чтобы урок засчитался.</summary>
    public const int MinJointMinutes = 30;

    private LessonSession()
    {
    }

    public Guid ExchangeId { get; private set; }
    public Guid TeacherId { get; private set; }
    public DateTime StartAt { get; private set; }

    /// <summary>Плановое окончание: начало + 45 минут. Хранится, чтобы проверка пересечений была обычным сравнением в SQL.</summary>
    public DateTime EndAt { get; private set; }

    public int DurationMin { get; private set; }
    public string? Topic { get; private set; }
    public SessionStatus Status { get; private set; }

    /// <summary>Кто предложил текущее время: ответить на него может только другой участник.</summary>
    public Guid ProposedById { get; private set; }

    /// <summary>Прежнее время, пока идёт перенос (показывается в карточке «перенос с…»).</summary>
    public DateTime? PreviousStartAt { get; private set; }

    /// <summary>Кто отменил; null — отмена без виновного (обмен прерван, заморожен или завершён).</summary>
    public Guid? CancelledById { get; private set; }

    public Guid? NoShowUserId { get; private set; }
    public int? LateJoinMinutes { get; private set; }

    /// <summary>Участник A (отправитель заявки) нажал «Урок состоялся».</summary>
    public bool ConfirmedByA { get; private set; }

    public bool ConfirmedByB { get; private set; }

    /// <summary>Когда урок стал неподтверждённым: отсюда идут 24 часа на ручное подтверждение.</summary>
    public DateTime? UnconfirmedAt { get; private set; }

    /// <summary>Напоминания уже отправлены (сбрасываются при переносе): повтор задачи не шлёт второе.</summary>
    public bool Reminder24Sent { get; private set; }

    public bool Reminder15Sent { get; private set; }

    /// <summary>Когда задача закрытия комнаты подвела итог урока. С этого момента статус окончательный (NoShow и Missed после этого уже не меняются): неявки считаются только окончательные.</summary>
    public DateTime? RoomClosedAt { get; private set; }

    /// <summary>Итог подведён: фиксирует момент (повторный вызов безвреден).</summary>
    public void MarkRoomClosed(DateTime now) => RoomClosedAt ??= now;

    public static LessonSession Propose(
        Guid exchangeId, Guid teacherId, Guid proposedById, DateTime startAt, string? topic, DateTime now, DateTime exchangeEnd)
    {
        var session = new LessonSession { ExchangeId = exchangeId, TeacherId = teacherId, ProposedById = proposedById, DurationMin = DurationMinutes };
        session.SetTime(startAt, now, exchangeEnd);
        session.Topic = CleanTopic(topic);
        session.Status = SessionStatus.Proposed;
        return session;
    }

    public DateTime ConfirmDeadline => (UnconfirmedAt ?? EndAt) + ManualConfirmWindow;

    /// <summary>Вход в комнату открывается (старт − 10 минут).</summary>
    public DateTime JoinOpensAt => StartAt - JoinOpensBefore;

    /// <summary>Вход закрывается и комната удаляется (плановое окончание + 30 минут = старт + 75).</summary>
    public DateTime RoomClosesAt => EndAt + JoinClosesAfterEnd;

    /// <summary>Момент предварительной проверки (старт + 15 минут).</summary>
    public DateTime FirstCheckAt => StartAt + FirstCheckAfterStart;

    /// <summary>Урок в таком статусе, что присутствие в комнате ещё учитывается (подтверждён и не закрыт).</summary>
    public bool AcceptsPresence => Status is SessionStatus.Scheduled or SessionStatus.InProgress or SessionStatus.NoShow or SessionStatus.Missed;

    /// <summary>Можно ли сейчас получить токен: урок подтверждён и идёт (или ещё может идти) и время в окне комнаты. Статус Missed и NoShow входят: опоздавший может зайти.</summary>
    public bool IsJoinableAt(DateTime now) =>
        AcceptsPresence
        && now >= JoinOpensAt && now < RoomClosesAt;

    /// <summary>Первый участник вошёл в комнату: Scheduled → InProgress. В любом другом статусе ничего не меняет (повторный вход, опоздавший).</summary>
    public bool MarkStarted()
    {
        if (Status != SessionStatus.Scheduled)
        {
            return false;
        }

        Status = SessionStatus.InProgress;
        return true;
    }

    /// <summary>
    /// Предварительная проверка на 15-й минуте (раздел 6.3 ТЗ). <paramref name="joinedIds"/> — кто из участников уже входил в комнату.
    /// Никого нет: Scheduled → Missed; зашёл один: InProgress → NoShow с виновником (не вошедший); зашли оба: ничего не меняется.
    /// </summary>
    /// <returns>true, если статус изменился.</returns>
    public bool ApplyFirstCheck(DateTime now, IReadOnlyCollection<Guid> joinedIds, Guid userA, Guid userB)
    {
        if (now < FirstCheckAt || Status is not (SessionStatus.Scheduled or SessionStatus.InProgress))
        {
            return false;
        }

        var aJoined = joinedIds.Contains(userA);
        var bJoined = joinedIds.Contains(userB);
        if (aJoined && bJoined)
        {
            return false;
        }

        if (!aJoined && !bJoined)
        {
            Status = SessionStatus.Missed;
            return true;
        }

        Status = SessionStatus.NoShow;
        NoShowUserId = aJoined ? userB : userA;
        return true;
    }

    /// <summary>
    /// Итог при закрытии комнаты (старт + 75 минут), раздел 6.3 ТЗ. <paramref name="firstJoins"/> — первое время входа каждого, кто входил;
    /// <paramref name="jointMinutes"/> — сколько минут оба пробыли в комнате одновременно.
    /// <list type="bullet">
    /// <item>≥ 30 минут: Completed (из InProgress, NoShow и Missed); опоздание второго сохраняется в <see cref="LateJoinMinutes"/>;</item>
    /// <item>оба были, но меньше 30: InProgress → Unconfirmed (ручное подтверждение 24 часа); NoShow и Missed остаются окончательными;</item>
    /// <item>вошёл один: NoShow с виновником; не вошёл никто: Missed (если первая проверка не успела, например задача пропущена).</item>
    /// </list>
    /// </summary>
    /// <returns>true, если статус изменился.</returns>
    public bool CloseRoom(DateTime now, IReadOnlyDictionary<Guid, DateTime> firstJoins, int jointMinutes, Guid userA, Guid userB)
    {
        if (now < RoomClosesAt || Status is not (SessionStatus.Scheduled or SessionStatus.InProgress or SessionStatus.NoShow or SessionStatus.Missed))
        {
            return false;
        }

        var aJoined = firstJoins.TryGetValue(userA, out var firstA);
        var bJoined = firstJoins.TryGetValue(userB, out var firstB);
        var before = Status;

        if (aJoined && bJoined && jointMinutes >= MinJointMinutes)
        {
            var late = (int)((firstA > firstB ? firstA : firstB) - StartAt).TotalMinutes;
            LateJoinMinutes = late > 0 ? late : null;
            Status = SessionStatus.Completed;
            return true;
        }

        if (aJoined && bJoined)
        {
            if (Status != SessionStatus.InProgress && Status != SessionStatus.Scheduled)
            {
                return false;   // NoShow / Missed с совместным временем < 30 остаются окончательными
            }

            Status = SessionStatus.Unconfirmed;
            UnconfirmedAt = now;
            return true;
        }

        if (Status == SessionStatus.Missed)
        {
            return false;   // Missed меняется только на Completed (оба успели ≥ 30 минут)
        }

        if (aJoined || bJoined)
        {
            Status = SessionStatus.NoShow;
            NoShowUserId = aJoined ? userB : userA;
        }
        else
        {
            Status = SessionStatus.Missed;
        }

        return Status != before;
    }

    /// <summary>Партнёр принимает предложенное время (из Proposed в Scheduled). Предлагавший принять своё же предложение не может.</summary>
    public void Confirm(Guid userId, DateTime now)
    {
        RequireStatus(SessionStatus.Scheduled, SessionStatus.Proposed);
        RequireAnswerer(userId);
        if (now >= StartAt)
        {
            throw new DomainValidationException("session_already_started", "Время начала уже наступило.");
        }

        Status = SessionStatus.Scheduled;
        ConfirmedByA = false;
        ConfirmedByB = false;
    }

    /// <summary>Партнёр отклоняет предложенное время.</summary>
    public void Decline(Guid userId)
    {
        RequireStatus(SessionStatus.Declined, SessionStatus.Proposed);
        RequireAnswerer(userId);
        Status = SessionStatus.Declined;
    }

    /// <summary>Время начала наступило, а ответа нет (фоновая задача): Proposed → Expired.</summary>
    public bool ExpireIfDue(DateTime now)
    {
        if (Status != SessionStatus.Proposed || now < StartAt)
        {
            return false;
        }

        Status = SessionStatus.Expired;
        return true;
    }

    /// <summary>
    /// Перенос запланированного урока: новое время ждёт подтверждения (Scheduled → Proposed), старое освобождается. Меньше чем за 2 часа до
    /// начала нельзя, только отмена. Предлагает любой из двух участников.
    /// </summary>
    public void Reschedule(Guid userId, DateTime newStartAt, DateTime now, DateTime exchangeEnd)
    {
        RequireStatus(SessionStatus.Proposed, SessionStatus.Scheduled);
        if (StartAt - now < FreeChangeWindow)
        {
            throw new DomainValidationException("reschedule_too_late", "Перенести урок можно не позднее чем за 2 часа до начала; позже его можно только отменить.");
        }

        var previous = StartAt;
        SetTime(newStartAt, now, exchangeEnd);
        PreviousStartAt = previous;
        ProposedById = userId;
        Status = SessionStatus.Proposed;
        ConfirmedByA = false;
        ConfirmedByB = false;
        Reminder24Sent = false;
        Reminder15Sent = false;
    }

    /// <summary>
    /// Отмена участником. Предложение отзывает автор (Proposed → Cancelled). Запланированный урок отменяется без последствий не позднее
    /// чем за 2 часа до начала (Cancelled), позже — LateCancelled (учитывается модератором). После начала отменять нельзя.
    /// </summary>
    public void Cancel(Guid userId, DateTime now)
    {
        if (Status == SessionStatus.Proposed)
        {
            if (userId != ProposedById)
            {
                throw new DomainValidationException("not_session_proposer", "Отозвать предложение может только его автор; чужое можно отклонить.");
            }

            Status = SessionStatus.Cancelled;
            CancelledById = userId;
            return;
        }

        RequireStatus(SessionStatus.Cancelled, SessionStatus.Scheduled);
        if (now >= StartAt)
        {
            throw new DomainValidationException("session_already_started", "Урок уже начался: отменить его нельзя.");
        }

        Status = StartAt - now >= FreeChangeWindow ? SessionStatus.Cancelled : SessionStatus.LateCancelled;
        CancelledById = userId;
    }

    /// <summary>Обмен прерван, заморожен или завершён: открытые уроки отменяются без виновного.</summary>
    public bool CancelByExchange()
    {
        if (Status is not (SessionStatus.Proposed or SessionStatus.Scheduled))
        {
            return false;
        }

        Status = SessionStatus.Cancelled;
        CancelledById = null;
        return true;
    }

    /// <summary>«Урок состоялся»: когда нажали оба, урок засчитан (Unconfirmed → Completed). Повтор тем же участником безвреден.</summary>
    /// <returns>true, если урок засчитан этим вызовом.</returns>
    public bool ConfirmManually(Guid userId, bool isParticipantA, DateTime now)
    {
        RequireStatus(SessionStatus.Completed, SessionStatus.Unconfirmed);
        if (now > ConfirmDeadline)
        {
            throw new DomainValidationException("confirm_window_closed", "Срок подтверждения (24 часа) истёк.");
        }

        if (isParticipantA)
        {
            ConfirmedByA = true;
        }
        else
        {
            ConfirmedByB = true;
        }

        if (!(ConfirmedByA && ConfirmedByB))
        {
            return false;
        }

        Status = SessionStatus.Completed;
        return true;
    }

    /// <summary>За 24 часа урок не подтвердили оба (фоновая задача): Unconfirmed → NotCounted.</summary>
    public bool ExpireUnconfirmedIfDue(DateTime now)
    {
        if (Status != SessionStatus.Unconfirmed || now < ConfirmDeadline)
        {
            return false;
        }

        Status = SessionStatus.NotCounted;
        return true;
    }

    public void MarkReminderSent(int minutesBefore)
    {
        if (minutesBefore == 1440)
        {
            Reminder24Sent = true;
        }
        else
        {
            Reminder15Sent = true;
        }
    }

    private void SetTime(DateTime startAt, DateTime now, DateTime exchangeEnd)
    {
        if (startAt < now + MinLeadTime)
        {
            throw new DomainValidationException("slot_too_soon", "Урок можно назначить не раньше чем за 1 час до начала.");
        }

        if (startAt > exchangeEnd)
        {
            throw new DomainValidationException("slot_after_exchange_end", "Время урока не может быть позже окончания обмена.");
        }

        StartAt = startAt;
        EndAt = startAt.AddMinutes(DurationMin);
    }

    private void RequireAnswerer(Guid userId)
    {
        if (userId == ProposedById)
        {
            throw new DomainValidationException("not_session_answerer", "Ответить на своё предложение нельзя: ответ даёт партнёр.");
        }
    }

    private void RequireStatus(SessionStatus to, params SessionStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
        {
            throw new InvalidStateTransitionException(nameof(LessonSession), Status.ToString(), to.ToString());
        }
    }

    private static string? CleanTopic(string? topic)
    {
        var text = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim();
        if (text is { Length: > TopicMaxLength })
        {
            throw new DomainValidationException("topic_too_long", $"Тема длиннее {TopicMaxLength} символов.");
        }

        return text;
    }
}
