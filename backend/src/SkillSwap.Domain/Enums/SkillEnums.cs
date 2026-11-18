namespace SkillSwap.Domain.Enums;

/// <summary>Куда относится навык в анкете: «Умею» (преподаю) или «Хочу изучать».</summary>
public enum SkillDirection
{
    Teach,
    Learn,
}

/// <summary>Уровень владения навыком (раздел 5.2 ТЗ): Начальный, Средний, Продвинутый. Порядок значений важен: больше — выше.</summary>
public enum SkillLevel
{
    Beginner = 1,
    Intermediate = 2,
    Advanced = 3,
}

/// <summary>Состояние предложения нового навыка (раздел 5.2 ТЗ): админ одобряет, объединяет с существующим или отклоняет.</summary>
public enum SuggestionStatus
{
    Pending,
    Approved,
    Merged,
    Rejected,
}

/// <summary>Статус заявки на обмен (раздел 9.1 ТЗ).</summary>
public enum ExchangeRequestStatus
{
    Pending,
    Accepted,
    Declined,
    Withdrawn,
    Expired,
}
