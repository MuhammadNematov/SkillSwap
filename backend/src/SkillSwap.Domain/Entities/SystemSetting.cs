using SkillSwap.Domain.Common;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Системная настройка (таблица SystemSettings, раздел 13 ТЗ): ключ и значение-строка, которые суперадмин меняет без выкладки кода.
/// Правка через API — этап 7; пока значения по умолчанию кладёт миграция.
/// </summary>
public class SystemSetting : ITimestamped
{
    public const int KeyMaxLength = 64;
    public const int ValueMaxLength = 1024;

    private SystemSetting()
    {
    }

    public string Key { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static SystemSetting Create(string key, string value, string description) =>
        new() { Key = key, Value = value, Description = description };

    public void ChangeValue(string value) => Value = value;
}

/// <summary>Ключи системных настроек.</summary>
public static class SystemSettingKeys
{
    /// <summary>
    /// «true» (по умолчанию): пользователи 16–17 лет подбираются только друг с другом, взрослые — только со взрослыми
    /// (раздел 10.4 ТЗ). «false»: возрастных ограничений в подборе нет.
    /// </summary>
    public const string MinorsMatchOnlyMinors = "Matching.MinorsMatchOnlyMinors";

    /// <summary>Не более стольких активных обменов одновременно (раздел 5.3 ТЗ). По умолчанию 3.</summary>
    public const string MaxActiveExchanges = "Requests.MaxActiveExchanges";

    /// <summary>Не более стольких ожидающих исходящих заявок. По умолчанию 5.</summary>
    public const string MaxPendingOutgoing = "Requests.MaxPendingOutgoing";

    /// <summary>Не более стольких новых заявок за сутки. По умолчанию 10.</summary>
    public const string MaxRequestsPerDay = "Requests.MaxPerDay";

    /// <summary>Через сколько дней после отказа можно снова написать тому же человеку. По умолчанию 30.</summary>
    public const string DeclineCooldownDays = "Requests.DeclineCooldownDays";
}
