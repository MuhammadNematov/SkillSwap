using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Options;

/// <summary>
/// Сроки урока, секция "Lessons". По умолчанию — значения ТЗ (<see cref="LessonTiming.Defaults"/>). Менять их можно <b>только в Development и Testing</b>
/// (для ручной проверки видеокомнаты без ожидания часов, см. <c>appsettings.Development.json</c>): в остальных средах другие значения не принимаются, приложение не запустится.
/// Все значения в минутах.
/// </summary>
public sealed class LessonTimingOptions
{
    public const string SectionName = "Lessons";

    /// <summary>Слот нельзя предложить раньше чем за столько минут до начала (по ТЗ 60).</summary>
    public int MinLeadMinutes { get; set; } = 60;

    /// <summary>Отмена и перенос без последствий не позднее чем за столько минут до начала (по ТЗ 120).</summary>
    public int FreeChangeMinutes { get; set; } = 120;

    /// <summary>Вход в комнату открывается за столько минут до начала (по ТЗ 10).</summary>
    public int JoinOpensBeforeMinutes { get; set; } = 10;

    /// <summary>Предварительная проверка «пришли ли оба» через столько минут после начала (по ТЗ 15).</summary>
    public int FirstCheckMinutes { get; set; } = 15;

    /// <summary>Комната закрывается через столько минут после планового окончания (по ТЗ 30).</summary>
    public int JoinClosesAfterEndMinutes { get; set; } = 30;

    /// <summary>Сколько минут оба должны провести вместе, чтобы урок засчитался (по ТЗ 30).</summary>
    public int MinJointMinutes { get; set; } = 30;

    /// <summary>Длительность урока (по ТЗ 45).</summary>
    public int DurationMinutes { get; set; } = 45;

    /// <summary>Сколько после закрытия комнаты можно подтвердить неподтверждённый урок вручную (по ТЗ 24 часа = 1440).</summary>
    public int ManualConfirmMinutes { get; set; } = 1440;

    public LessonTimingSettings ToSettings() => new(
        TimeSpan.FromMinutes(MinLeadMinutes), TimeSpan.FromMinutes(FreeChangeMinutes), TimeSpan.FromMinutes(JoinOpensBeforeMinutes),
        TimeSpan.FromMinutes(FirstCheckMinutes), TimeSpan.FromMinutes(JoinClosesAfterEndMinutes), MinJointMinutes, DurationMinutes,
        TimeSpan.FromMinutes(ManualConfirmMinutes));

    /// <summary>Совпадает ли набор со значениями ТЗ.</summary>
    public bool IsDefault => ToSettings() == LessonTiming.Defaults;

    /// <summary>Текст ошибки, если значения недопустимы; null, если всё в порядке.</summary>
    public string? Validate()
    {
        if (MinLeadMinutes < 0 || FreeChangeMinutes < 0 || JoinOpensBeforeMinutes < 0 || JoinClosesAfterEndMinutes < 0 || ManualConfirmMinutes < 1)
        {
            return "Lessons: минуты не могут быть отрицательными (ManualConfirmMinutes — не меньше 1).";
        }

        if (DurationMinutes is < 1 or > 240)
        {
            return "Lessons:DurationMinutes должен быть от 1 до 240.";
        }

        if (MinJointMinutes < 1 || MinJointMinutes > DurationMinutes)
        {
            return "Lessons:MinJointMinutes должен быть от 1 до DurationMinutes: иначе урок нельзя засчитать.";
        }

        if (FirstCheckMinutes < 1 || FirstCheckMinutes > DurationMinutes + JoinClosesAfterEndMinutes)
        {
            return "Lessons:FirstCheckMinutes должен быть от 1 и не позже закрытия комнаты.";
        }

        return null;
    }
}
