namespace SkillSwap.Domain.Enums;

/// <summary>Статус отзыва (таблица Reviews, раздел 8.1 ТЗ). Pending — написан, но скрыт до слепой публикации; Frozen — опубликован, но не считается в рейтинге, пока идёт спор.</summary>
public enum ReviewStatus
{
    Pending,
    Published,
    Hidden,
    Frozen,
}

/// <summary>Тип обращения (раздел 8.2 ТЗ).</summary>
public enum CaseType
{
    /// <summary>Спор по отзыву: несправедливая, оскорбительная или ложная оценка. Обычный приоритет.</summary>
    ReviewDispute,

    /// <summary>Жалоба на поведение: оскорбления, угрозы, домогательства, спам, вымогательство. Высокий приоритет.</summary>
    Conduct,

    /// <summary>Неявки и срыв обмена: систематическое уклонение, недобросовестные уроки. Обычный приоритет.</summary>
    NoShowAbuse,
}

/// <summary>Категория обращения (на подаче выбирается из списка своего типа).</summary>
public enum CaseCategory
{
    ReviewUnfair,
    ReviewAbusive,
    ReviewFalse,
    Insults,
    Threats,
    Harassment,
    Spam,
    Extortion,
    SystematicEvasion,
    BadFaithLessons,
}

/// <summary>Приоритет: высокий — срок реакции 24 часа, обычный — 72 часа. Любая жалоба с участием пользователя 16–17 лет высокого приоритета.</summary>
public enum CasePriority
{
    Normal,
    High,
}

/// <summary>Статус обращения (раздел 9.4 ТЗ).</summary>
public enum CaseStatus
{
    Open,
    Withdrawn,
    InProgress,
    WaitingResponse,
    Resolved,
    Appealed,
    AppealReview,
    Closed,
}

/// <summary>Решение по обращению (раздел 8.3 ТЗ). «Предупреждение» можно сочетать с остальными решениями флагом <c>AlsoWarned</c>.</summary>
public enum CaseDecision
{
    /// <summary>Оставить отзыв: критика конструктивна и не нарушает правил.</summary>
    ReviewKept,

    /// <summary>Скрыть отзыв: оскорбления, клевета или очевидная месть.</summary>
    ReviewHidden,

    /// <summary>Предупреждение нарушителю (в профиль; 3 активных за 12 месяцев создают запрос на бан).</summary>
    Warning,

    /// <summary>Принудительно завершить обмен (из активного или замороженного состояния).</summary>
    ExchangeEnded,

    /// <summary>Заморозить обмен и запросить бан у админа.</summary>
    FrozenBanRequested,

    /// <summary>Нарушения нет: обращение закрыто без санкций (жалобы на поведение и неявки).</summary>
    NoViolation,
}

/// <summary>Основание доступа модератора к чату (таблица ChatAccessGrants).</summary>
public enum GrantReason
{
    Case,
    AppealReview,
}

/// <summary>Статус запроса на бан (таблица BanRequests).</summary>
public enum BanRequestStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>Итог апелляции, который выносит админ (таблица CaseAppeals).</summary>
public enum AppealOutcome
{
    /// <summary>Решение модератора остаётся.</summary>
    Upheld,

    /// <summary>Решение отменено: последствия возвращаются, нарушения нет.</summary>
    Overturned,

    /// <summary>Решение заменено другим (см. <c>FinalDecision</c>).</summary>
    Changed,
}
