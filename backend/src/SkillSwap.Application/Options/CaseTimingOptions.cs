using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Options;

/// <summary>
/// Сроки обращений (секция <c>Cases</c>). Значения по умолчанию равны ТЗ. Другие допустимы только в Development и Testing (для ручной проверки таймеров:
/// пояснения, напоминание, апелляция, срок реакции); в боевой среде приложение с другими значениями не запустится.
/// </summary>
public sealed class CaseTimingOptions
{
    public const string SectionName = "Cases";

    /// <summary>Срок реакции на обращение высокого приоритета в минутах (по ТЗ 24 часа = 1440).</summary>
    public int HighSlaMinutes { get; set; } = 1440;

    /// <summary>Срок реакции на обычное обращение в минутах (по ТЗ 72 часа = 4320).</summary>
    public int NormalSlaMinutes { get; set; } = 4320;

    /// <summary>Срок ответа на вопрос модератора в минутах (по ТЗ 48 часов = 2880).</summary>
    public int ExplanationWindowMinutes { get; set; } = 2880;

    /// <summary>За сколько минут до конца срока ответа напоминание (6 часов = 360).</summary>
    public int ExplanationReminderBeforeMinutes { get; set; } = 360;

    /// <summary>Срок апелляции и закрытия обращения после решения в минутах (по ТЗ 7 дней = 10080).</summary>
    public int AppealWindowMinutes { get; set; } = 10080;

    public CaseTimingSettings ToSettings() => new(
        TimeSpan.FromMinutes(HighSlaMinutes), TimeSpan.FromMinutes(NormalSlaMinutes), TimeSpan.FromMinutes(ExplanationWindowMinutes),
        TimeSpan.FromMinutes(ExplanationReminderBeforeMinutes), TimeSpan.FromMinutes(AppealWindowMinutes));

    /// <summary>Совпадает ли набор со значениями ТЗ.</summary>
    public bool IsDefault => ToSettings() == CaseTiming.Defaults;

    /// <summary>Текст ошибки, если значения недопустимы; null, если всё в порядке.</summary>
    public string? Validate()
    {
        if (HighSlaMinutes < 1 || NormalSlaMinutes < 1 || ExplanationWindowMinutes < 1 || ExplanationReminderBeforeMinutes < 1 || AppealWindowMinutes < 1)
        {
            return "Cases: сроки задаются в минутах и не могут быть меньше 1.";
        }

        if (ExplanationReminderBeforeMinutes >= ExplanationWindowMinutes)
        {
            return "Cases:ExplanationReminderBeforeMinutes должен быть меньше ExplanationWindowMinutes: иначе напоминание придёт раньше вопроса.";
        }

        return null;
    }
}
