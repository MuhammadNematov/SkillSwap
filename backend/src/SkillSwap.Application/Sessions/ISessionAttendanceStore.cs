using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Sessions;

public interface ISessionAttendanceStore
{
    /// <summary>Интервалы присутствия урока с отслеживанием, по времени входа.</summary>
    Task<IReadOnlyList<SessionAttendance>> ListForUpdateAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Интервалы присутствия уроков без отслеживания (для итога урока и показа).</summary>
    Task<IReadOnlyList<SessionAttendance>> ListAsync(Guid sessionId, CancellationToken ct);

    /// <summary>Кто сейчас в комнате (открытый интервал присутствия) по каждому из уроков; уроков без присутствующих в словаре нет.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ListInRoomAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct);

    void Add(SessionAttendance attendance);

    void Remove(SessionAttendance attendance);

    Task SaveAsync(CancellationToken ct);
}
