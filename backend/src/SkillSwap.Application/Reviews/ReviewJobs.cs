using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Notifications;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Reviews;

/// <summary>
/// Что делает бэкенд с отзывами сам (раздел 8.1 ТЗ): публикует отзыв через 7 дней после первого, если партнёр не написал, приглашает написать отзыв
/// после закрытия обмена и напоминает за 2 дня до срока. Задачи идемпотентны (повторный запуск ничего не меняет) и сверяются с состоянием, а не с
/// тем, что было при постановке.
/// </summary>
public sealed class ReviewJobs
{
    /// <summary>За сколько до конца срока (14 дней) приходит напоминание.</summary>
    public static readonly TimeSpan ReminderBeforeDeadline = TimeSpan.FromDays(2);

    private readonly IReviewStore _reviews;
    private readonly IExchangeStore _exchanges;
    private readonly ReviewService _service;
    private readonly IUnitOfWork _uow;
    private readonly INotificationPublisher _notify;
    private readonly TimeProvider _clock;

    public ReviewJobs(IReviewStore reviews, IExchangeStore exchanges, ReviewService service, IUnitOfWork uow, INotificationPublisher notify, TimeProvider clock)
    {
        _reviews = reviews;
        _exchanges = exchanges;
        _service = service;
        _uow = uow;
        _notify = notify;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Отзыв ждал партнёра 7 дней: публикуется (Pending → Published) и пересчитывается рейтинг адресата. Опубликованный или ещё не созревший не трогается.</summary>
    public async Task PublishDueAsync(Guid reviewId)
    {
        var peek = await _reviews.FindAsync(reviewId, CancellationToken.None);
        if (peek is null || peek.Status != ReviewStatus.Pending)
        {
            return;
        }

        var exchange = await _exchanges.GetViewAsync(peek.ExchangeId, CancellationToken.None);
        if (exchange is null)
        {
            return;
        }

        await _uow.ExecuteAsync(async ct =>
        {
            await _uow.LockUsersAsync([peek.AuthorId, peek.TargetId], ct);
            var review = await _reviews.FindAsync(reviewId, ct);
            if (review is null || !review.IsDueForPublication(Now()))
            {
                return 0;
            }

            review.Publish(Now());
            await _reviews.SaveAsync(ct);
            await _service.AfterPublishAsync([review], exchange, ct);
            await _reviews.SaveAsync(ct);   // уведомление адресату
            return 0;
        }, CancellationToken.None);

        _notify.Dispatch();
    }

    /// <summary>Обмен закрыт: каждому, у кого есть право на отзыв и кто ещё не написал, приходит «можно оставить отзыв». Один раз на обмен.</summary>
    public Task InviteAsync(Guid exchangeId) => NotifyAsync(exchangeId, NotificationType.ReviewAvailable, e => e.MarkReviewInvitesSent(Now()));

    /// <summary>За 2 дня до конца срока: напоминание тем, кто имеет право и всё ещё не написал. Один раз на обмен.</summary>
    public Task RemindAsync(Guid exchangeId) => NotifyAsync(exchangeId, NotificationType.ReviewReminder, e => e.MarkReviewReminderSent(Now()));

    private async Task NotifyAsync(Guid exchangeId, NotificationType type, Func<Exchange, bool> mark)
    {
        var peek = await _exchanges.GetViewAsync(exchangeId, CancellationToken.None);
        if (peek is null || peek.Status is not (ExchangeStatus.Completed or ExchangeStatus.Aborted))
        {
            return;
        }

        await _uow.ExecuteAsync(async ct =>
        {
            await _uow.LockUsersAsync([peek.UserA.Id, peek.UserB.Id], ct);
            var exchange = await _exchanges.FindAsync(exchangeId, ct);
            if (exchange is null || !mark(exchange))
            {
                return 0;   // уже рассылали
            }

            var view = await _exchanges.GetViewAsync(exchangeId, ct) ?? peek;
            var now = Now();
            foreach (var (user, partner) in new[] { (view.UserA, view.UserB), (view.UserB, view.UserA) })
            {
                var eligibility = _service.Evaluate(await _service.ContextAsync(view, user.Id, ct), now);
                var written = await _reviews.FindByAuthorAsync(exchangeId, user.Id, tracked: false, ct);
                if (eligibility.CanWrite && written is null)
                {
                    _notify.Add(user.Id, type, new { exchangeId, userId = partner.Id, userName = partner.Name, deadlineAt = eligibility.DeadlineAt });
                }
            }

            await _exchanges.SaveAsync(ct);
            return 0;
        }, CancellationToken.None);

        _notify.Dispatch();
    }
}

/// <summary>Ставит задачи отзывов, когда обмен закрылся (завершён по сроку или кнопкой, прерван участником, блокировкой или модератором).</summary>
public sealed class ReviewScheduler
{
    private static readonly TimeSpan InviteDelay = TimeSpan.FromSeconds(30);

    private readonly IBackgroundJobs _jobs;
    private readonly TimeProvider _clock;

    public ReviewScheduler(IBackgroundJobs jobs, TimeProvider clock)
    {
        _jobs = jobs;
        _clock = clock;
    }

    /// <summary>
    /// Вызывать после фиксации закрытия. Приглашение уходит через полминуты (транзакция закрытия к этому времени завершена), напоминание
    /// ставится на срок отзыва минус 2 дня; обе задачи сами проверяют, что обмен закрыт и право на отзыв есть.
    /// </summary>
    public void OnExchangeClosed(Guid exchangeId, DateTime closedAt)
    {
        var now = _clock.GetUtcNow();
        _jobs.Schedule<ReviewJobs>(j => j.InviteAsync(exchangeId), now + InviteDelay);

        var remindAt = new DateTimeOffset(closedAt + Review.WriteWindow - ReviewJobs.ReminderBeforeDeadline, TimeSpan.Zero);
        if (remindAt > now)
        {
            _jobs.Schedule<ReviewJobs>(j => j.RemindAsync(exchangeId), remindAt);
        }
    }
}
