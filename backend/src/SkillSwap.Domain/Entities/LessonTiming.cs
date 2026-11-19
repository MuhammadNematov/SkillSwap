namespace SkillSwap.Domain.Entities;

/// <summary>Сроки урока (разделы 6.2, 6.3 и 7.2 ТЗ) одним набором.</summary>
public sealed record LessonTimingSettings(
    TimeSpan MinLeadTime,
    TimeSpan FreeChangeWindow,
    TimeSpan JoinOpensBefore,
    TimeSpan FirstCheckAfterStart,
    TimeSpan JoinClosesAfterEnd,
    int MinJointMinutes,
    int DurationMinutes,
    TimeSpan ManualConfirmWindow);

/// <summary>
/// Действующие сроки урока. По умолчанию — значения ТЗ (слот не раньше чем через час, вход за 10 минут, проверка на 15-й минуте, комната закрывается
/// через 30 минут после окончания, зачёт от 30 минут вместе, урок 45 минут). Для ручной проверки видеокомнаты в разработке их можно сократить
/// настройками <c>Lessons:*</c> (<c>appsettings.Development.json</c>): приложение применяет их при старте, вне Development и Testing другие значения не принимаются.
/// Значение общее на весь процесс и не меняется во время работы.
/// </summary>
public static class LessonTiming
{
    public static readonly LessonTimingSettings Defaults = new(
        MinLeadTime: TimeSpan.FromHours(1),
        FreeChangeWindow: TimeSpan.FromHours(2),
        JoinOpensBefore: TimeSpan.FromMinutes(10),
        FirstCheckAfterStart: TimeSpan.FromMinutes(15),
        JoinClosesAfterEnd: TimeSpan.FromMinutes(30),
        MinJointMinutes: 30,
        DurationMinutes: 45,
        ManualConfirmWindow: TimeSpan.FromHours(24));

    private static volatile LessonTimingSettings _current = Defaults;

    public static LessonTimingSettings Current => _current;

    public static void Configure(LessonTimingSettings settings) => _current = settings;

    public static void Reset() => _current = Defaults;
}
