namespace SkillSwap.Domain.Entities;

/// <summary>Сроки обращений (раздел 8.2 ТЗ) одним набором.</summary>
/// <param name="HighPrioritySla">Срок реакции на обращение высокого приоритета (по ТЗ 24 часа).</param>
/// <param name="NormalPrioritySla">Срок реакции на обычное обращение (по ТЗ 72 часа).</param>
/// <param name="ExplanationWindow">Сколько у сторон на ответ по запросу модератора (по ТЗ 48 часов).</param>
/// <param name="ExplanationReminderBefore">За сколько до конца срока ответа приходит напоминание (6 часов).</param>
/// <param name="AppealWindow">Сколько после решения можно подать апелляцию; без неё обращение закрывается через то же время (по ТЗ 7 дней).</param>
public sealed record CaseTimingSettings(
    TimeSpan HighPrioritySla,
    TimeSpan NormalPrioritySla,
    TimeSpan ExplanationWindow,
    TimeSpan ExplanationReminderBefore,
    TimeSpan AppealWindow);

/// <summary>
/// Действующие сроки обращений. По умолчанию значения ТЗ; для ручной проверки в разработке их можно сократить настройками <c>Cases:*</c>
/// (<c>appsettings.Development.json</c>): приложение применяет их при старте, вне Development и Testing другие значения не принимаются.
/// Сроки подачи (14, 30 и 90 дней) не меняются. Значение общее на весь процесс и не меняется во время работы.
/// </summary>
public static class CaseTiming
{
    public static readonly CaseTimingSettings Defaults = new(
        HighPrioritySla: TimeSpan.FromHours(24),
        NormalPrioritySla: TimeSpan.FromHours(72),
        ExplanationWindow: TimeSpan.FromHours(48),
        ExplanationReminderBefore: TimeSpan.FromHours(6),
        AppealWindow: TimeSpan.FromDays(7));

    private static volatile CaseTimingSettings _current = Defaults;

    public static CaseTimingSettings Current => _current;

    public static void Configure(CaseTimingSettings settings) => _current = settings;

    public static void Reset() => _current = Defaults;
}
