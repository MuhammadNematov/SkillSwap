using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Matching;

/// <summary>
/// Возрастные группы (раздел 10.4 ТЗ): 16–17 лет и 18+. Пока включена настройка <c>Matching.MinorsMatchOnlyMinors</c>,
/// группы не пересекаются: подросток не видит взрослых, а взрослый — подростков.
/// </summary>
public static class AgeGroups
{
    /// <summary>
    /// Граница: родившийся позже этой даты младше 18 лет. Одна и та же граница нужна и в памяти, и в SQL,
    /// чтобы правила не разошлись.
    /// </summary>
    public static DateOnly AdultBirthCutoff(DateOnly today) => today.AddYears(-User.AdultAge);

    public static bool IsMinor(DateOnly birthDate, DateOnly today) => birthDate > AdultBirthCutoff(today);

    /// <summary>Может ли <paramref name="viewer"/> видеть <paramref name="target"/> по возрасту при включённой настройке.</summary>
    public static bool CanSeeEachOther(User viewer, User target, bool minorsOnlyWithMinors, DateOnly today)
    {
        if (!minorsOnlyWithMinors)
        {
            return true;
        }

        if (viewer.DateOfBirth is not { } a || target.DateOfBirth is not { } b)
        {
            return false;   // у служебных аккаунтов даты рождения нет, и участвовать в подборе они не могут
        }

        return IsMinor(a, today) == IsMinor(b, today);
    }
}
