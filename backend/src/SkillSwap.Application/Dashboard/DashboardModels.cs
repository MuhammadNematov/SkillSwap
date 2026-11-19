using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Dashboard;

/// <summary>Главная страница кабинета: всё нужное одним запросом (раздел 8 ТЗ). Все даты в UTC.</summary>
public sealed record DashboardDto(
    NextSessionDto? NextSession,
    DashboardCountersDto Counters,
    DashboardStatsDto Stats,
    DashboardRatingDto Rating,
    IReadOnlyList<ActionNeededDto> ActionsNeeded);

/// <summary>Ближайший подтверждённый урок.</summary>
/// <param name="CanJoinFrom">Когда откроется вход в комнату (за 10 минут до начала, раздел 7.2 ТЗ). Видео подключается на этапе 5.</param>
/// <param name="CanJoinUntil">Когда закроется (через 30 минут после планового окончания).</param>
public sealed record NextSessionDto(
    Guid Id,
    Guid ExchangeId,
    DateTime StartAt,
    DateTime EndAt,
    int DurationMin,
    string? Topic,
    SessionStatus Status,
    bool IAmTeacher,
    RequestUserDto Partner,
    DateTime CanJoinFrom,
    DateTime CanJoinUntil);

/// <param name="ActiveExchanges">Обмены, занимающие место в лимите (активные и замороженные).</param>
/// <param name="IncomingRequests">Ожидающие ответа входящие заявки (срок не вышел).</param>
/// <param name="OutgoingRequests">Ожидающие ответа исходящие заявки.</param>
/// <param name="UnreadMessages">Непрочитанные сообщения во всех доступных чатах (чужие, не удалённые).</param>
public sealed record DashboardCountersDto(int ActiveExchanges, int IncomingRequests, int OutgoingRequests, int UnreadMessages, int UnreadNotifications);

/// <param name="CompletedSessions">Засчитанные уроки (<c>Completed</c>) во всех обменах.</param>
/// <param name="TaughtMinutes">Минуты уроков, которые провёл я.</param>
/// <param name="LearnedMinutes">Минуты уроков, на которых учился я.</param>
/// <param name="CompletedExchanges">Завершённые обмены (<c>Completed</c>; прерванные не считаются).</param>
public sealed record DashboardStatsDto(int CompletedSessions, int TaughtMinutes, int LearnedMinutes, int CompletedExchanges);

/// <param name="Average">Средняя оценка; <c>null</c> у «Новичка» (раздел 8.1 ТЗ).</param>
public sealed record DashboardRatingDto(decimal? Average, int Count, bool IsNewcomer);

/// <summary>То, что ждёт ответа пользователя. Тип определяет, какие поля заполнены.</summary>
/// <param name="Type">
/// <c>AnswerRequest</c> (входящая заявка, <c>requestId</c>, <c>dueAt</c> — срок ответа), <c>AnswerSession</c> (партнёр предложил время, <c>sessionId</c>, <c>exchangeId</c>,
/// <c>dueAt</c> — начало урока), <c>ConfirmSession</c> («Урок состоялся», <c>dueAt</c> — до какого времени можно), <c>FinishExchange</c> (партнёр нажал «Завершить»),
/// <c>AnswerExtension</c> (партнёр предлагает продлить). <c>WriteReview</c> появится вместе с отзывами.
/// </param>
public sealed record ActionNeededDto(string Type, Guid? SessionId, Guid? ExchangeId, Guid? RequestId, DateTime? DueAt);

public static class ActionTypes
{
    public const string AnswerRequest = "AnswerRequest";
    public const string AnswerSession = "AnswerSession";
    public const string ConfirmSession = "ConfirmSession";
    public const string FinishExchange = "FinishExchange";
    public const string AnswerExtension = "AnswerExtension";
    public const string WriteReview = "WriteReview";
}

/// <summary>Сырые данные для главной: считаются несколькими небольшими запросами в хранилище.</summary>
public sealed record DashboardData(
    NextSessionRow? NextSession,
    DashboardCountersDto Counters,
    DashboardStatsDto Stats,
    IReadOnlyList<ActionNeededDto> Actions);

public sealed record NextSessionRow(Domain.Entities.LessonSession Session, Guid ExchangeId, Guid PartnerId);

public interface IDashboardStore
{
    Task<DashboardData> LoadAsync(Guid userId, DateTime now, CancellationToken ct);
}
