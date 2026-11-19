using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Отзыв после обмена (таблица Reviews, раздел 8.1 ТЗ): 1–5 баллов по четырём критериям и текст 20–1000 символов. Отзывы слепые: написанный отзыв
/// скрыт (Pending) и становится виден, когда написаны оба или через 7 дней после первого; после публикации править нельзя. Рейтинг считают только
/// опубликованные (Published): отзыв в споре (Frozen) виден, но в рейтинг не входит, скрытый (Hidden) не виден и не входит.
/// Все переходы статуса — только методами этого класса.
/// </summary>
public class Review : BaseEntity
{
    public const int ScoreMin = 1;
    public const int ScoreMax = 5;
    public const int TextMinLength = 20;
    public const int TextMaxLength = 1000;

    /// <summary>Сколько дней после закрытия обмена можно написать отзыв.</summary>
    public static readonly TimeSpan WriteWindow = TimeSpan.FromDays(14);

    /// <summary>Через сколько после первого отзыва он публикуется, даже если второй не написан.</summary>
    public static readonly TimeSpan BlindWindow = TimeSpan.FromDays(7);

    private Review()
    {
    }

    public Guid ExchangeId { get; private set; }
    public Guid AuthorId { get; private set; }
    public Guid TargetId { get; private set; }
    public int ScoreKnowledge { get; private set; }
    public int ScoreExplaining { get; private set; }
    public int ScorePunctuality { get; private set; }
    public int ScorePoliteness { get; private set; }
    public string Text { get; private set; } = string.Empty;

    /// <summary>Отметка «Полный обмен»: каждый из двоих провёл не меньше трёх засчитанных уроков преподавателем.</summary>
    public bool IsFullExchange { get; private set; }

    public ReviewStatus Status { get; private set; } = ReviewStatus.Pending;
    public DateTime? PublishedAt { get; private set; }

    /// <summary>Средняя из четырёх оценок: столько этот отзыв весит в рейтинге адресата.</summary>
    public decimal Average => (ScoreKnowledge + ScoreExplaining + ScorePunctuality + ScorePoliteness) / 4m;

    /// <summary>Входит ли отзыв в рейтинг: только опубликованный и не оспариваемый.</summary>
    public bool CountsInRating => Status == ReviewStatus.Published;

    /// <summary>Виден ли отзыв другим людям (опубликованный, в том числе пока идёт спор).</summary>
    public bool IsVisible => Status is ReviewStatus.Published or ReviewStatus.Frozen;

    public static Review Create(
        Guid exchangeId, Guid authorId, Guid targetId, int knowledge, int explaining, int punctuality, int politeness, string? text, bool isFullExchange)
    {
        if (authorId == targetId)
        {
            throw new DomainValidationException("review_self", "Отзыв о самом себе невозможен.");
        }

        var review = new Review { ExchangeId = exchangeId, AuthorId = authorId, TargetId = targetId, IsFullExchange = isFullExchange };
        review.Fill(knowledge, explaining, punctuality, politeness, text);
        return review;
    }

    /// <summary>Правка автором — только пока отзыв не опубликован.</summary>
    public void Edit(int knowledge, int explaining, int punctuality, int politeness, string? text)
    {
        if (Status != ReviewStatus.Pending)
        {
            throw new DomainValidationException("review_already_published", "Опубликованный отзыв править нельзя.");
        }

        Fill(knowledge, explaining, punctuality, politeness, text);
    }

    /// <summary>Слепая публикация: Pending → Published. Повторный вызов для уже опубликованного ничего не меняет.</summary>
    /// <returns>true, если отзыв стал опубликованным этим вызовом.</returns>
    public bool Publish(DateTime now)
    {
        if (Status != ReviewStatus.Pending)
        {
            return false;
        }

        Status = ReviewStatus.Published;
        PublishedAt = now;
        return true;
    }

    /// <summary>Спор по отзыву: влияние на рейтинг замораживается (Published → Frozen).</summary>
    public void Freeze()
    {
        RequireStatus(ReviewStatus.Frozen, ReviewStatus.Published);
        Status = ReviewStatus.Frozen;
    }

    /// <summary>Оставить отзыв по итогам спора (Frozen → Published).</summary>
    public void Unfreeze()
    {
        RequireStatus(ReviewStatus.Published, ReviewStatus.Frozen);
        Status = ReviewStatus.Published;
    }

    /// <summary>Скрыть: оскорбления, клевета или очевидная месть (из Published или Frozen).</summary>
    public void Hide()
    {
        RequireStatus(ReviewStatus.Hidden, ReviewStatus.Published, ReviewStatus.Frozen);
        Status = ReviewStatus.Hidden;
    }

    /// <summary>Вернуть скрытый отзыв, если админ отменил решение по апелляции (Hidden → Published).</summary>
    public void Restore()
    {
        RequireStatus(ReviewStatus.Published, ReviewStatus.Hidden);
        Status = ReviewStatus.Published;
    }

    private void Fill(int knowledge, int explaining, int punctuality, int politeness, string? text)
    {
        foreach (var score in new[] { knowledge, explaining, punctuality, politeness })
        {
            if (score is < ScoreMin or > ScoreMax)
            {
                throw new DomainValidationException("review_score_out_of_range", $"Оценка — от {ScoreMin} до {ScoreMax}.");
            }
        }

        var clean = text?.Trim() ?? string.Empty;
        if (clean.Length < TextMinLength)
        {
            throw new DomainValidationException("review_text_too_short", $"Текст отзыва — не меньше {TextMinLength} символов.");
        }

        if (clean.Length > TextMaxLength)
        {
            throw new DomainValidationException("review_text_too_long", $"Текст отзыва — не больше {TextMaxLength} символов.");
        }

        ScoreKnowledge = knowledge;
        ScoreExplaining = explaining;
        ScorePunctuality = punctuality;
        ScorePoliteness = politeness;
        Text = clean;
    }

    private void RequireStatus(ReviewStatus to, params ReviewStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
        {
            throw new InvalidStateTransitionException(nameof(Review), Status.ToString(), to.ToString());
        }
    }
}
