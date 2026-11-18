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

    /// <summary>
    /// Плановое окончание прошло (фоновая задача). Пока нет событий LiveKit (этап 5), урок не подтверждён автоматически:
    /// Scheduled → Unconfirmed, дальше участники подтверждают его вручную в течение 24 часов.
    /// </summary>
    public bool MarkUnconfirmedIfDue(DateTime now)
    {
        if (Status != SessionStatus.Scheduled || now < EndAt)
        {
            return false;
        }

        Status = SessionStatus.Unconfirmed;
        UnconfirmedAt = now;
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
