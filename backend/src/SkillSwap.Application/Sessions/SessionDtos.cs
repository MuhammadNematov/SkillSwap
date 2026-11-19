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
/// <param name="NoShowUserId">Кто не пришёл на урок (у NoShow; остаётся и если опоздавший потом засчитан).</param>
/// <param name="ParticipantsInRoom">Кто из участников сейчас в видеокомнате (id пользователей): по нему фронтенд показывает «партнёр в комнате». Заполняется в событии <c>sessionUpdated</c> и в ответах <c>GET /sessions/{id}</c>, <c>GET /exchanges/{id}/sessions</c>; в карточках внутри истории чата и в календаре пусто.</param>
/// <param name="LateJoinMinutes">На сколько минут опоздал второй участник, если урок засчитан (иначе null).</param>
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
    DateTime CreatedAt,
    Guid? NoShowUserId = null,
    int? LateJoinMinutes = null)
{
    /// <summary>Кто сейчас в видеокомнате (id пользователей); пусто, если никого или если ответ это не заполняет (см. описание).</summary>
    public IReadOnlyList<Guid> ParticipantsInRoom { get; init; } = [];
}

/// <summary>Урок в календаре: с партнёром по обмену.</summary>
public sealed record CalendarSessionDto(LessonSessionDto Session, RequestUserDto Partner);

/// <summary>Урок из хранилища вместе с участниками его обмена.</summary>
public sealed record SessionRow(Domain.Entities.LessonSession Session, Guid UserAId, Guid UserBId);

/// <summary>Токен входа в видеокомнату урока (POST /sessions/{id}/join-token).</summary>
/// <param name="Token">Короткий токен LiveKit; передайте его клиенту LiveKit вместе с <paramref name="Url"/>.</param>
/// <param name="Url">Адрес медиасервера (WebSocket).</param>
/// <param name="RoomName">Имя комнаты: <c>session-{id урока}</c>.</param>
/// <param name="ExpiresAt">До какого времени токен годится для входа (после входа сеанс от него не зависит).</param>
/// <param name="StartAt">Плановое начало урока.</param>
/// <param name="PlannedEndAt">Плановое окончание (начало + 45 минут): по нему фронтенд сам показывает «осталось 5 минут» на 40-й минуте.</param>
/// <param name="RoomClosesAt">Когда комната закрывается и удаляется (начало + 75 минут).</param>
public sealed record JoinTokenDto(
    string Token, string Url, string RoomName, DateTime ExpiresAt, DateTime StartAt, DateTime PlannedEndAt, DateTime RoomClosesAt);
