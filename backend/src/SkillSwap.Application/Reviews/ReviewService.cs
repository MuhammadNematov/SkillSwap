using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Files;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Application.Reviews;

/// <summary>
/// Отзывы после обмена (раздел 8.1 ТЗ): кто и когда может написать, слепая публикация (когда написаны оба или через 7 дней после первого) и рейтинг.
/// Право на отзыв считается на сервере заново при каждом запросе (<see cref="ReviewRules"/>); запись идёт в транзакции под блокировкой обоих участников,
/// поэтому два одновременных отзыва партнёров публикуются вместе, а повторный отзыв автора невозможен.
/// </summary>
public sealed class ReviewService
{
    /// <summary>Как давно закрытый обмен ещё может давать право на отзыв (срок 14 дней).</summary>
    private static readonly TimeSpan LookBack = Review.WriteWindow;

    private readonly IReviewStore _reviews;
    private readonly IExchangeStore _exchanges;
    private readonly ILessonSessionStore _sessions;
    private readonly IProfileStore _profiles;
    private readonly ProfileService _profile;
    private readonly IUnitOfWork _uow;
    private readonly IBackgroundJobs _jobs;
    private readonly INotificationPublisher _notify;
    private readonly AvatarUrls _avatars;
    private readonly TimeProvider _clock;

    public ReviewService(
        IReviewStore reviews, IExchangeStore exchanges, ILessonSessionStore sessions, IProfileStore profiles, ProfileService profile, IUnitOfWork uow,
        IBackgroundJobs jobs, INotificationPublisher notify, AvatarUrls avatars, TimeProvider clock)
    {
        _reviews = reviews;
        _exchanges = exchanges;
        _sessions = sessions;
        _profiles = profiles;
        _profile = profile;
        _uow = uow;
        _jobs = jobs;
        _notify = notify;
        _avatars = avatars;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    // ---------- чтение ----------

    /// <summary>Мои отзывы по обмену: что я могу, что написал, что опубликовано обо мне.</summary>
    public async Task<ExchangeReviewsDto> GetStateAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var exchange = await RequireParticipantAsync(userId, exchangeId, ct);
        var partnerId = PartnerOf(exchange, userId);

        var eligibility = Evaluate(await ContextAsync(exchange, userId, ct), Now());
        var mine = await _reviews.FindByAuthorAsync(exchangeId, userId, tracked: false, ct);
        var aboutMe = await _reviews.FindByAuthorAsync(exchangeId, partnerId, tracked: false, ct);   // отзыв партнёра обо мне
        var urls = await _avatars.ForViewerAsync(userId, [(exchange.UserA.Id, exchange.UserA.AvatarUrl), (exchange.UserB.Id, exchange.UserB.AvatarUrl)], ct);

        var canEdit = mine is { Status: ReviewStatus.Pending } && eligibility.DeadlineAt is { } deadline && Now() <= deadline;
        return new ExchangeReviewsDto(
            exchangeId,
            CanWrite: mine is null && eligibility.CanWrite,
            UnavailableReason: mine is not null ? ReviewUnavailable.AlreadyReviewed : eligibility.Reason,
            eligibility.IsFullExchange,
            eligibility.OneSided,
            eligibility.DeadlineAt,
            canEdit,
            mine is null ? null : ToDto(mine, exchange, urls, forAuthor: true),
            aboutMe is { IsVisible: true } ? ToDto(aboutMe, exchange, urls, forAuthor: false) : null);   // слепые отзывы: до публикации партнёрский не виден
    }

    /// <summary>Опубликованные отзывы о человеке. Видят те же люди, что и его профиль (скрытие, блокировки, возрастные группы), иначе 404.</summary>
    public async Task<UserReviewsDto> ListForUserAsync(Guid viewerId, bool viewerIsRegular, Guid targetId, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        var target = await _profile.RequireVisibleAsync(viewerId, viewerIsRegular, targetId, ct);

        var (items, total) = await _reviews.ListVisibleForTargetAsync(targetId, page, pageSize, ct);
        var authors = await _profiles.FindUsersAsync(items.Select(r => r.AuthorId).Distinct().ToList(), ct);
        var urls = await _avatars.ForViewerAsync(viewerId, authors.Values.Where(a => a.Status != UserStatus.Deleted).Select(a => (a.Id, a.AvatarKey)).ToList(), ct);

        var dtos = items.Select(r =>
        {
            var author = authors.GetValueOrDefault(r.AuthorId);
            var deleted = author is null || author.Status == UserStatus.Deleted;
            return new PublicReviewDto(
                r.Id,
                new RequestUserDto(r.AuthorId, deleted ? User.DeletedDisplayName : author!.Name, deleted ? null : urls.GetValueOrDefault(r.AuthorId), deleted),
                r.ScoreKnowledge, r.ScoreExplaining, r.ScorePunctuality, r.ScorePoliteness, r.Average, r.Text, r.IsFullExchange, r.PublishedAt!.Value);
        }).ToList();

        return new UserReviewsDto(dtos, page, pageSize, total, target.IsNewcomer ? null : target.RatingAvg, target.RatingCount, target.IsNewcomer);
    }

    /// <summary>Обмены, по которым я могу написать отзыв и ещё не написал: для главной страницы (действие <c>WriteReview</c>).</summary>
    public async Task<IReadOnlyList<ReviewInvitation>> PendingInvitationsAsync(Guid userId, CancellationToken ct)
    {
        var now = Now();
        var closed = await _exchanges.ListClosedSinceAsync(userId, now - LookBack, ct);
        if (closed.Count == 0)
        {
            return [];
        }

        var written = await _reviews.ListWrittenExchangeIdsAsync(userId, closed.Select(c => c.Id).ToList(), ct);
        var contexts = await ContextsAsync(closed, userId, ct);
        var result = new List<ReviewInvitation>();
        foreach (var exchange in closed.Where(c => !written.Contains(c.Id)))
        {
            var eligibility = Evaluate(contexts[exchange.Id], now);
            if (eligibility.CanWrite && eligibility.DeadlineAt is { } deadline)
            {
                result.Add(new ReviewInvitation(exchange.Id, deadline));
            }
        }

        return result;
    }

    // ---------- запись ----------

    /// <summary>
    /// Оставить отзыв. Публикуется сразу, если партнёр уже написал свой (оба видны одновременно) или писать больше некому (одностороннее право);
    /// иначе ждёт партнёра, а через 7 дней публикуется сам.
    /// </summary>
    public async Task<ReviewDto> CreateAsync(Guid userId, Guid exchangeId, ReviewRequest request, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var peek = await RequireParticipantAsync(userId, exchangeId, ct);
        var partnerId = PartnerOf(peek, userId);

        Guid reviewId = Guid.Empty;
        DateTime? scheduleAt = null;
        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([userId, partnerId], token);

            if (await _reviews.FindByAuthorAsync(exchangeId, userId, tracked: false, token) is not null)
            {
                throw new ConflictException(ReviewUnavailable.AlreadyReviewed, "Вы уже оставили отзыв об этом обмене.");
            }

            var exchange = await _exchanges.GetViewAsync(exchangeId, token) ?? throw new NotFoundException("exchange_not_found", "Обмен не найден.");
            var context = await ContextAsync(exchange, userId, token);
            var now = Now();
            var eligibility = Evaluate(context, now);
            if (!eligibility.CanWrite)
            {
                throw new ConflictException(eligibility.Reason!, ReasonText(eligibility.Reason!));
            }

            var review = Review.Create(
                exchangeId, userId, partnerId, request.ScoreKnowledge, request.ScoreExplaining, request.ScorePunctuality, request.ScorePoliteness,
                request.Text, eligibility.IsFullExchange, now);
            _reviews.Add(review);

            // Слепая публикация: партнёр уже написал (в любом состоянии) или писать больше некому.
            var partnerReview = await _reviews.FindByAuthorAsync(exchangeId, partnerId, tracked: true, token);
            var published = new List<Review>();
            if (partnerReview is not null || eligibility.OneSided)
            {
                review.Publish(now);
                published.Add(review);
                if (partnerReview is not null && partnerReview.Publish(now))
                {
                    published.Add(partnerReview);
                }
            }
            else
            {
                scheduleAt = review.PublishDueAt;
            }

            await _reviews.SaveAsync(token);
            await AfterPublishAsync(published, exchange, token);
            await _reviews.SaveAsync(token);   // уведомления, добавленные при публикации
            reviewId = review.Id;
            return 0;
        }, ct);

        _notify.Dispatch();
        if (scheduleAt is { } at)
        {
            var id = reviewId;
            _jobs.Schedule<ReviewJobs>(j => j.PublishDueAsync(id), new DateTimeOffset(at, TimeSpan.Zero));
        }

        return (await GetStateAsync(userId, exchangeId, ct)).MyReview!;
    }

    /// <summary>Изменить свой отзыв: пока он не опубликован и срок (14 дней) не вышел.</summary>
    public async Task<ReviewDto> UpdateAsync(Guid userId, Guid exchangeId, ReviewRequest request, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var peek = await RequireParticipantAsync(userId, exchangeId, ct);
        var partnerId = PartnerOf(peek, userId);

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([userId, partnerId], token);
            var review = await _reviews.FindByAuthorAsync(exchangeId, userId, tracked: true, token)
                ?? throw new NotFoundException("review_not_found", "Вы ещё не писали отзыв об этом обмене.");
            if (review.Status != ReviewStatus.Pending)
            {
                throw new ConflictException("review_already_published", "Опубликованный отзыв править нельзя.");
            }

            var exchange = await _exchanges.GetViewAsync(exchangeId, token) ?? throw new NotFoundException("exchange_not_found", "Обмен не найден.");
            var eligibility = Evaluate(await ContextAsync(exchange, userId, token), Now());
            if (eligibility.DeadlineAt is { } deadline && Now() > deadline)
            {
                throw new ConflictException(ReviewUnavailable.WindowClosed, ReasonText(ReviewUnavailable.WindowClosed));
            }

            review.Edit(request.ScoreKnowledge, request.ScoreExplaining, request.ScorePunctuality, request.ScorePoliteness, request.Text);
            await _reviews.SaveAsync(token);
            return 0;
        }, ct);

        return (await GetStateAsync(userId, exchangeId, ct)).MyReview!;
    }

    // ---------- общее с задачами ----------

    /// <summary>
    /// После публикации: пересчёт рейтинга адресатов и уведомление «о вас опубликован отзыв». Вызывается внутри транзакции под блокировкой обоих участников.
    /// </summary>
    internal async Task AfterPublishAsync(IReadOnlyCollection<Review> published, ExchangeRow exchange, CancellationToken ct)
    {
        foreach (var review in published)
        {
            await RecalculateRatingAsync(review.TargetId, ct);
            var author = exchange.UserA.Id == review.AuthorId ? exchange.UserA : exchange.UserB;
            _notify.Add(review.TargetId, NotificationType.ReviewPublished, new { exchangeId = review.ExchangeId, reviewId = review.Id, userId = author.Id, userName = author.Name });
        }
    }

    /// <summary>Пересчитывает <c>RatingAvg</c> и <c>RatingCount</c> человека по опубликованным и не оспариваемым отзывам (среднее из средних по четырём критериям).</summary>
    internal async Task RecalculateRatingAsync(Guid userId, CancellationToken ct)
    {
        var aggregate = await _reviews.AggregateAsync(userId, ct);
        await _reviews.ApplyRatingAsync(userId, Math.Round(aggregate.Average, 2, MidpointRounding.AwayFromZero), aggregate.Count, ct);
    }

    internal ReviewEligibility Evaluate(ReviewContext context, DateTime now) => ReviewRules.Evaluate(context, now);

    /// <summary>Данные обмена с точки зрения участника: засчитанные уроки, преподавание, окончательные неявки.</summary>
    internal async Task<ReviewContext> ContextAsync(ExchangeRow exchange, Guid userId, CancellationToken ct) =>
        (await ContextsAsync([exchange], userId, ct))[exchange.Id];

    internal async Task<IReadOnlyDictionary<Guid, ReviewContext>> ContextsAsync(IReadOnlyCollection<ExchangeRow> exchanges, Guid userId, CancellationToken ct)
    {
        var ids = exchanges.Select(e => e.Id).ToList();
        var completed = await _sessions.CountCompletedAsync(ids, ct);
        var noShows = await _sessions.CountFinalNoShowsAsync(ids, ct);

        return exchanges.ToDictionary(e => e.Id, e =>
        {
            var partnerId = PartnerOf(e, userId);
            var partner = e.UserA.Id == partnerId ? e.UserA : e.UserB;
            var mine = completed.GetValueOrDefault((e.Id, userId));
            var theirs = completed.GetValueOrDefault((e.Id, partnerId));
            return new ReviewContext(
                e.Status, e.ClosedAt, partner.IsDeleted, mine + theirs, mine, theirs,
                noShows.GetValueOrDefault((e.Id, partnerId)), noShows.GetValueOrDefault((e.Id, userId)));
        });
    }

    internal static string ReasonText(string reason) => reason switch
    {
        ReviewUnavailable.ExchangeNotClosed => "Отзыв можно написать, когда обмен завершён или прерван.",
        ReviewUnavailable.PartnerDeleted => "Партнёр удалил аккаунт: отзыв о нём написать нельзя.",
        ReviewUnavailable.NoCountedSessions => "В обмене не было ни одной засчитанной сессии: отзывы недоступны.",
        ReviewUnavailable.WindowClosed => "Срок отзыва (14 дней после закрытия обмена) истёк.",
        ReviewUnavailable.AlreadyReviewed => "Вы уже оставили отзыв об этом обмене.",
        _ => "Отзыв сейчас написать нельзя.",
    };

    internal static Guid PartnerOf(ExchangeRow exchange, Guid userId) => exchange.UserA.Id == userId ? exchange.UserB.Id : exchange.UserA.Id;

    private ReviewDto ToDto(Review r, ExchangeRow exchange, IReadOnlyDictionary<Guid, string?> urls, bool forAuthor)
    {
        var author = exchange.UserA.Id == r.AuthorId ? exchange.UserA : exchange.UserB;
        var status = forAuthor || r.Status != ReviewStatus.Frozen ? r.Status : ReviewStatus.Published;   // адресат не видит, что отзыв оспаривается
        return new ReviewDto(
            r.Id, r.ExchangeId, author with { AvatarUrl = urls.GetValueOrDefault(author.Id) }, r.TargetId,
            r.ScoreKnowledge, r.ScoreExplaining, r.ScorePunctuality, r.ScorePoliteness, r.Average, r.Text, r.IsFullExchange,
            status, r.CreatedAt, r.PublishedAt, r.Status == ReviewStatus.Pending ? r.PublishDueAt : null);
    }

    private async Task<ExchangeRow> RequireParticipantAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        var exchange = await _exchanges.GetViewAsync(exchangeId, ct);
        if (exchange is null || (exchange.UserA.Id != userId && exchange.UserB.Id != userId))
        {
            throw new NotFoundException("exchange_not_found", "Обмен не найден.");
        }

        return exchange;
    }

    private async Task RequireActiveRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Отзывы доступны только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }
    }
}
