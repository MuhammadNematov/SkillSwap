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
