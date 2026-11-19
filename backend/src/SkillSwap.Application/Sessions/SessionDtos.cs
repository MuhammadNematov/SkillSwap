using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Sessions;

/// <summary>Предложить время урока. <c>StartAt</c> — момент начала с часовым поясом (ISO 8601), сервер хранит UTC.</summary>
/// <param name="TeacherId">Кто проводит этот урок (один из двух участников).</param>
public sealed record ProposeSessionRequest(DateTimeOffset? StartAt, Guid? TeacherId, string? Topic);

/// <summary>Новое время при переносе.</summary>
public sealed record RescheduleSessionRequest(DateTimeOffset? StartAt);

/// <summary>Урок (раздел 9.3 ТЗ). Время в UTC, клиент показывает его в своём часовом поясе.</summary>
/// <param name="ProposedById">Кто предложил текущее время: ответить на него может другой участник.</param>
/// <param name="PreviousStartAt">Прежнее время, пока идёт перенос.</param>
/// <param name="CancelledById">Кто отменил; null — отмена без виновного (обмен прерван, заморожен или завершён).</param>
/// <param name="ConfirmedBy">Кто из участников нажал «Урок состоялся» (для неподтверждённого урока).</param>
/// <param name="ConfirmDeadlineAt">До какого времени можно подтвердить урок вручную (у Unconfirmed).</param>
public sealed record LessonSessionDto(
    Guid Id,
    Guid ExchangeId,
    Guid TeacherId,
    DateTime StartAt,
    DateTime EndAt,
    int DurationMinutes,
    string? Topic,
    SessionStatus Status,
    Guid ProposedById,
    DateTime? PreviousStartAt,
    Guid? CancelledById,
    IReadOnlyList<Guid> ConfirmedBy,
    DateTime? UnconfirmedAt,
    DateTime? ConfirmDeadlineAt,
    DateTime CreatedAt);

/// <summary>Урок в календаре: с партнёром по обмену.</summary>
public sealed record CalendarSessionDto(LessonSessionDto Session, RequestUserDto Partner);

/// <summary>Урок из хранилища вместе с участниками его обмена.</summary>
public sealed record SessionRow(Domain.Entities.LessonSession Session, Guid UserAId, Guid UserBId);
