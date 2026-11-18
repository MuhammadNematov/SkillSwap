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

/// <summary>Статус обмена (раздел 9.2 ТЗ).</summary>
public enum ExchangeStatus
{
    Active,
    Completed,
    Aborted,
    Frozen,
}

/// <summary>Тип уведомления (таблица Notifications, раздел 13 ТЗ). Текст строит фронтенд по типу и данным (<c>PayloadJson</c>).</summary>
public enum NotificationType
{
    /// <summary>Мне прислали заявку на обмен.</summary>
    RequestReceived,

    /// <summary>Мою заявку приняли: открыт обмен.</summary>
    RequestAccepted,

    /// <summary>Мою заявку отклонили.</summary>
    RequestDeclined,

    /// <summary>Моя заявка истекла без ответа.</summary>
    RequestExpired,

    /// <summary>Партнёр нажал «Завершить» и ждёт меня.</summary>
    ExchangeFinishRequested,

    /// <summary>Обмен завершён (оба нажали «Завершить» или вышел срок).</summary>
    ExchangeCompleted,

    /// <summary>Обмен прерван: партнёром (с причиной) либо по причине «партнёр недоступен».</summary>
    ExchangeAborted,

    /// <summary>Партнёр предлагает продлить обмен на 30 дней.</summary>
    ExtensionProposed,

    /// <summary>Обмен продлён.</summary>
    ExchangeExtended,
}
