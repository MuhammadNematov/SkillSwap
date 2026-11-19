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

    /// <summary>Непрочитанные сообщения в чате обмена: одно уведомление на обмен, пока есть непрочитанные (новые сообщения обновляют его).</summary>
    NewMessages,

    /// <summary>Партнёр предложил время урока (или новое время при переносе).</summary>
    SessionProposed,

    /// <summary>Партнёр подтвердил время урока.</summary>
    SessionConfirmed,

    /// <summary>Партнёр отклонил предложенное время.</summary>
    SessionDeclined,

    /// <summary>Партнёр отменил урок (поле late: меньше чем за 2 часа).</summary>
    SessionCancelled,

    /// <summary>Предложенное время истекло без ответа.</summary>
    SessionExpired,

    /// <summary>Напоминание об уроке: за 24 часа и за 15 минут (дублируется письмом).</summary>
    SessionReminder,

    /// <summary>Урок закончился, но факт не подтверждён: нажмите «Урок состоялся» в течение 24 часов.</summary>
    SessionNeedsConfirmation,

    /// <summary>Урок засчитан.</summary>
    SessionCompleted,

    /// <summary>Урок не засчитан: за 24 часа его не подтвердили оба.</summary>
    SessionNotCounted,

    /// <summary>Партнёр не пришёл на урок (проверка на 15-й минуте или при закрытии комнаты); в данных userId — не пришедший. Опоздавший ещё может зайти.</summary>
    SessionNoShow,

    /// <summary>На урок не пришёл никто (проверка на 15-й минуте).</summary>
    SessionMissed,

    /// <summary>Обмен закрыт, можно оставить отзыв (14 дней). В данных: exchangeId, userId и userName партнёра, deadlineAt.</summary>
    ReviewAvailable,

    /// <summary>Срок отзыва заканчивается (за 2 дня), вы ещё не написали.</summary>
    ReviewReminder,

    /// <summary>О вас опубликован отзыв (слепая публикация: написали оба или прошло 7 дней). В данных: exchangeId, reviewId, userId и userName автора.</summary>
    ReviewPublished,

    /// <summary>Обмен заморожен: по нему подана жалоба из видеозвонка. В данных: exchangeId.</summary>
    ExchangeFrozen,

    /// <summary>Обмен разморожен (обращение отозвано или решено в пользу продолжения). В данных: exchangeId.</summary>
    ExchangeUnfrozen,

    /// <summary>Модератор взял обращение в работу и получил доступ к переписке этого обмена (одно уведомление каждому из двоих). В данных: caseId, exchangeId, role (Opener или Against).</summary>
    CaseTaken,

    /// <summary>Модератор задал вам вопрос по обращению: ответить нужно за 48 часов. В данных: caseId, exchangeId, questionId, deadlineAt.</summary>
    CaseExplanationRequested,
}

/// <summary>Тип сообщения чата (таблица Messages, раздел 13 ТЗ). В шаге 5 используются Text; остальные подключаются в шагах 6 и 7.</summary>
public enum MessageType
{
    Text,
    Voice,
    File,
    SessionCard,
    System,
}

/// <summary>Вид вложения чата: картинка (png, jpg, webp), голосовое (WebM/Opus) или файл (pdf, docx, xlsx, pptx, zip, txt).</summary>
public enum AttachmentKind
{
    File,
    Voice,
    Image,
}

/// <summary>Статус урока (раздел 9.3 ТЗ). InProgress, Missed и NoShow заполняются событиями LiveKit (этап 5, раздел 6.3 ТЗ).</summary>
public enum SessionStatus
{
    Proposed,
    Scheduled,
    Declined,
    Expired,
    Cancelled,
    LateCancelled,
    InProgress,
    Missed,
    NoShow,
    Completed,
    Unconfirmed,
    NotCounted,
}

/// <summary>Состояние выгрузки данных пользователя (POST /me/export).</summary>
public enum DataExportStatus
{
    Pending,
    Processing,
    Ready,
    Failed,
    Expired,
}
