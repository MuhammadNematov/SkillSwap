using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Reviews;

/// <summary>Отзыв: четыре оценки 1–5 (знание предмета, умение объяснять, пунктуальность, вежливость) и текст 20–1000 символов.</summary>
public sealed record ReviewRequest(int ScoreKnowledge, int ScoreExplaining, int ScorePunctuality, int ScorePoliteness, string? Text);

/// <summary>Отзыв глазами его автора или адресата в карточке обмена.</summary>
/// <param name="Status">Для автора настоящий (Pending — ждёт публикации, Published, Hidden — скрыт модератором); адресату отзыв показывается только опубликованный.</param>
/// <param name="PublishAt">Пока отзыв ждёт публикации: когда он опубликуется сам, если партнёр не напишет; иначе null.</param>
public sealed record ReviewDto(
    Guid Id,
    Guid ExchangeId,
    RequestUserDto Author,
    Guid TargetId,
    int ScoreKnowledge,
    int ScoreExplaining,
    int ScorePunctuality,
    int ScorePoliteness,
    decimal Average,
    string Text,
    bool IsFullExchange,
    ReviewStatus Status,
    DateTime CreatedAt,
    DateTime? PublishedAt,
    DateTime? PublishAt);

/// <summary>
/// Что можно и что есть по отзывам этого обмена для меня (<c>GET /exchanges/{id}/reviews</c>).
/// </summary>
/// <param name="CanWrite">Я могу написать отзыв прямо сейчас.</param>
/// <param name="UnavailableReason">Если нельзя: <c>exchange_not_closed</c>, <c>partner_deleted</c>, <c>no_counted_sessions</c>, <c>review_window_closed</c>, <c>review_already_exists</c>.</param>
/// <param name="IsFullExchange">Мой отзыв получит отметку «Полный обмен».</param>
/// <param name="OneSided">Право только у меня (партнёр не провёл ни одного засчитанного урока, а я пострадал от его неявок): отзыв опубликуется сразу.</param>
/// <param name="DeadlineAt">До какого времени можно написать или изменить отзыв (закрытие обмена + 14 дней).</param>
/// <param name="CanEdit">Мой отзыв ещё не опубликован, его можно изменить.</param>
/// <param name="MyReview">Мой отзыв (виден мне всегда).</param>
/// <param name="ReviewAboutMe">Отзыв партнёра обо мне: только после публикации (слепые отзывы).</param>
public sealed record ExchangeReviewsDto(
    Guid ExchangeId,
    bool CanWrite,
    string? UnavailableReason,
    bool IsFullExchange,
    bool OneSided,
    DateTime? DeadlineAt,
    bool CanEdit,
    ReviewDto? MyReview,
    ReviewDto? ReviewAboutMe);

/// <summary>Опубликованный отзыв в профиле человека (<c>GET /users/{id}/reviews</c>). Автор удалённого аккаунта подписан «Удалённый пользователь».</summary>
public sealed record PublicReviewDto(
    Guid Id,
    RequestUserDto Author,
    int ScoreKnowledge,
    int ScoreExplaining,
    int ScorePunctuality,
    int ScorePoliteness,
    decimal Average,
    string Text,
    bool IsFullExchange,
    DateTime PublishedAt);

/// <summary>Страница отзывов о человеке вместе с его рейтингом (у «Новичка», меньше трёх отзывов, средней оценки нет).</summary>
public sealed record UserReviewsDto(IReadOnlyList<PublicReviewDto> Items, int Page, int PageSize, int TotalCount, decimal? RatingAvg, int RatingCount, bool IsNewcomer);

/// <summary>Приглашение написать отзыв (главная страница: действие <c>WriteReview</c>).</summary>
public sealed record ReviewInvitation(Guid ExchangeId, DateTime DeadlineAt);

/// <summary>Итог по опубликованным отзывам человека: сколько и средняя из средних.</summary>
public sealed record RatingAggregate(int Count, decimal Average);

/// <summary>Хранилище отзывов (таблица Reviews).</summary>
public interface IReviewStore
{
    void Add(Review review);

    /// <summary>Отзыв по id с отслеживанием (для публикации по сроку).</summary>
    Task<Review?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Отзыв автора об этом обмене; с отслеживанием, если его собираются менять.</summary>
    Task<Review?> FindByAuthorAsync(Guid exchangeId, Guid authorId, bool tracked, CancellationToken ct);

    /// <summary>Мои отзывы по этим обменам (какие из обменов я уже отрецензировал).</summary>
    Task<IReadOnlySet<Guid>> ListWrittenExchangeIdsAsync(Guid authorId, IReadOnlyCollection<Guid> exchangeIds, CancellationToken ct);

    /// <summary>Опубликованные и оспариваемые отзывы о человеке: новые сверху. Скрытые и ещё не опубликованные не возвращаются.</summary>
    Task<(IReadOnlyList<Review> Items, int Total)> ListVisibleForTargetAsync(Guid targetId, int page, int pageSize, CancellationToken ct);

    /// <summary>Сколько опубликованных (не оспариваемых и не скрытых) отзывов о человеке и их средняя оценка.</summary>
    Task<RatingAggregate> AggregateAsync(Guid targetId, CancellationToken ct);

    /// <summary>Записывает рейтинг в профиль (<c>Users.RatingAvg</c>, <c>RatingCount</c>).</summary>
    Task ApplyRatingAsync(Guid userId, decimal average, int count, CancellationToken ct);

    /// <summary>Отзывы автора и о нём для выгрузки данных (раздел 8.1 ТЗ, право на забвение: выгрузка своих данных).</summary>
    Task<IReadOnlyList<Review>> ListWrittenByAsync(Guid authorId, CancellationToken ct);

    Task<IReadOnlyList<Review>> ListReceivedAsync(Guid targetId, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);
}
