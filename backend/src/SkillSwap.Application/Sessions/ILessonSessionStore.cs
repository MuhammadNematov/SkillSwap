using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Sessions;

public interface ILessonSessionStore
{
    void Add(LessonSession session);

    /// <summary>Урок для изменения (с отслеживанием). Менять только под блокировкой обоих участников.</summary>
    Task<LessonSession?> FindAsync(Guid id, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);

    /// <summary>Уроки по их id (для показа карточек в истории чата).</summary>
    Task<IReadOnlyDictionary<Guid, LessonSession>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Все уроки обмена: ближайшие по времени сверху вниз (новые начала впереди).</summary>
    Task<IReadOnlyList<LessonSession>> ListByExchangeAsync(Guid exchangeId, CancellationToken ct);

    /// <summary>
    /// Уроки пользователя во всех его обменах, начинающиеся в <c>[from, to)</c>. Без <paramref name="includeClosed"/> отклонённые,
    /// истёкшие и отменённые не возвращаются.
    /// </summary>
    Task<IReadOnlyList<SessionRow>> ListForUserAsync(Guid userId, DateTime from, DateTime to, bool includeClosed, CancellationToken ct);

    /// <summary>
    /// Кто из перечисленных пользователей в это время уже занят другим подтверждённым уроком (в любом их обмене, кроме
    /// <paramref name="excludeSessionId"/>). Занятость: Scheduled и InProgress, отрезки <c>[start, end)</c> пересекаются.
    /// </summary>
    Task<IReadOnlySet<Guid>> BusyUsersAsync(IReadOnlyCollection<Guid> userIds, DateTime start, DateTime end, Guid? excludeSessionId, CancellationToken ct);

    /// <summary>Сколько в обмене сейчас предложений без ответа (защита от спама слотами).</summary>
    Task<int> CountProposedAsync(Guid exchangeId, CancellationToken ct);

    /// <summary>Открытые уроки обмена (Proposed и Scheduled) с отслеживанием: их отменяют при прерывании, заморозке и завершении обмена.</summary>
    Task<IReadOnlyList<LessonSession>> FindOpenForExchangeAsync(Guid exchangeId, CancellationToken ct);

    /// <summary>Идущий урок (InProgress) обмена, в комнате которого пользователь находится прямо сейчас (интервал присутствия без выхода); null, если такого нет.</summary>
    Task<Guid?> FindInCallSessionAsync(Guid exchangeId, Guid userId, CancellationToken ct);

    /// <summary>Сколько засчитанных (Completed) уроков провёл каждый преподаватель в каждом из обменов (баланс, раздел 6.1 ТЗ).</summary>
    Task<IReadOnlyDictionary<(Guid ExchangeId, Guid TeacherId), int>> CountCompletedAsync(IReadOnlyCollection<Guid> exchangeIds, CancellationToken ct);

    /// <summary>
    /// Сколько окончательных неявок у каждого участника в каждом из обменов: уроки в статусе NoShow, итог которых уже подведён при закрытии комнаты.
    /// Предварительная неявка на 15-й минуте не считается: опоздавший ещё может зайти, и тогда урок станет Completed. Ключ — (обмен, не пришедший).
    /// </summary>
    Task<IReadOnlyDictionary<(Guid ExchangeId, Guid UserId), int>> CountFinalNoShowsAsync(IReadOnlyCollection<Guid> exchangeIds, CancellationToken ct);
}
