using SkillSwap.Domain.Enums;

namespace SkillSwap.Domain.Entities;

/// <summary>Причина, по которой отзыв сейчас написать нельзя (коды идут в ответ API как есть).</summary>
public static class ReviewUnavailable
{
    public const string ExchangeNotClosed = "exchange_not_closed";
    public const string PartnerDeleted = "partner_deleted";
    public const string NoCountedSessions = "no_counted_sessions";
    public const string WindowClosed = "review_window_closed";
    public const string AlreadyReviewed = "review_already_exists";
}

/// <summary>Итог проверки «можно ли мне оставить отзыв об этом обмене» (раздел 8.1 ТЗ).</summary>
/// <param name="OneSided">Право только у меня (партнёр не провёл ни одного засчитанного урока, а я пострадал от его неявок): отзыв публикуется сразу.</param>
/// <param name="IsFullExchange">Отметка «Полный обмен».</param>
/// <param name="DeadlineAt">До какого времени можно написать (закрытие обмена + 14 дней); null, пока обмен не закрыт.</param>
public sealed record ReviewEligibility(bool CanWrite, string? Reason, bool OneSided, bool IsFullExchange, DateTime? DeadlineAt);

/// <summary>Данные обмена, по которым решается право на отзыв. Всё о «мне» и «партнёре» считается с точки зрения проверяемого участника.</summary>
/// <param name="CompletedSessions">Сколько засчитанных (Completed) уроков в обмене всего.</param>
/// <param name="MyTaughtCompleted">Сколько засчитанных уроков провёл я как преподаватель.</param>
/// <param name="PartnerTaughtCompleted">То же у партнёра.</param>
/// <param name="PartnerFinalNoShows">Окончательные неявки партнёра в этом обмене.</param>
/// <param name="MyFinalNoShows">Мои окончательные неявки (по ним проверяется право партнёра).</param>
public sealed record ReviewContext(
    ExchangeStatus Status,
    DateTime? ClosedAt,
    bool PartnerDeleted,
    int CompletedSessions,
    int MyTaughtCompleted,
    int PartnerTaughtCompleted,
    int PartnerFinalNoShows,
    int MyFinalNoShows);

/// <summary>
/// Правила «когда можно оставить отзыв» (раздел 8.1 ТЗ). Обмен завершён штатно или прерван (в том числе блокировкой: заблокировать партнёра первым и этим
/// избежать плохого отзыва нельзя) и была хотя бы одна засчитанная сессия — отзыв доступен обоим. Если сессий не было, но у партнёра три окончательные
/// неявки, отзыв доступен только пострадавшему («в одностороннем порядке»). Иначе недоступен. Срок — 14 дней после закрытия обмена.
/// Отзыв об удалившем аккаунт не пишется.
/// </summary>
public static class ReviewRules
{
    /// <summary>Сколько окончательных неявок партнёра дают пострадавшему одностороннее право (то же число, что у выхода из обмена).</summary>
    public const int NoShowThreshold = Exchange.NoShowAbortThreshold;

    /// <summary>Сколько засчитанных уроков должен провести преподавателем каждый, чтобы отзыв получил отметку «Полный обмен».</summary>
    public const int FullExchangeLessons = 3;

    public static ReviewEligibility Evaluate(ReviewContext c, DateTime now)
    {
        if (c.Status is not (ExchangeStatus.Completed or ExchangeStatus.Aborted) || c.ClosedAt is null)
        {
            return new ReviewEligibility(false, ReviewUnavailable.ExchangeNotClosed, false, false, null);
        }

        var deadline = c.ClosedAt.Value + Review.WriteWindow;
        var full = c.Status == ExchangeStatus.Completed && c.MyTaughtCompleted >= FullExchangeLessons && c.PartnerTaughtCompleted >= FullExchangeLessons;

        if (c.PartnerDeleted)
        {
            return new ReviewEligibility(false, ReviewUnavailable.PartnerDeleted, false, false, deadline);
        }

        bool oneSided;
        if (c.CompletedSessions >= 1)
        {
            oneSided = false;   // право у обоих
        }
        else if (c.PartnerFinalNoShows >= NoShowThreshold)
        {
            oneSided = !PartnerHasRight(c);   // сессий не было, пострадал я: пишу только я (если партнёр сам не пострадал от моих неявок)
        }
        else
        {
            return new ReviewEligibility(false, ReviewUnavailable.NoCountedSessions, false, false, deadline);
        }

        if (now > deadline)
        {
            return new ReviewEligibility(false, ReviewUnavailable.WindowClosed, oneSided, full, deadline);
        }

        return new ReviewEligibility(true, null, oneSided, full, deadline);
    }

    /// <summary>
    /// Есть ли у партнёра право на отзыв (его собственная проверка, но без срока и без «партнёр удалён»): нужно, чтобы решить, публиковать ли мой отзыв сразу
    /// (писать больше некому) или ждать слепых правил.
    /// </summary>
    public static bool PartnerHasRight(ReviewContext fromMyPointOfView) =>
        fromMyPointOfView.CompletedSessions >= 1 || fromMyPointOfView.MyFinalNoShows >= NoShowThreshold;
}
