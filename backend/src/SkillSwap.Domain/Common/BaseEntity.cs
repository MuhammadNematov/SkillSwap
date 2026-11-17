namespace SkillSwap.Domain.Common;

/// <summary>
/// Базовая сущность: у всех таблиц есть Id (uuid) и метки времени в UTC (раздел 13 ТЗ).
/// </summary>
public abstract class BaseEntity : ITimestamped
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
