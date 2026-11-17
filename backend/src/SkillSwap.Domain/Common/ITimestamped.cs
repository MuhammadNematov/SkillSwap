namespace SkillSwap.Domain.Common;

/// <summary>
/// Сущность с метками времени в UTC. Их проставляет AppDbContext при сохранении.
/// </summary>
public interface ITimestamped
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}
