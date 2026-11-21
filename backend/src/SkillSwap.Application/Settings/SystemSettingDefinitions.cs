using System.Globalization;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Settings;

public enum SettingKind
{
    Bool,
    Int,
}

/// <summary>Описание одной системной настройки: тип, допустимый диапазон, значение по умолчанию. Менять через API можно только эти настройки.</summary>
public sealed record SystemSettingDefinition(string Key, SettingKind Kind, string Default, int? Min, int? Max, string Description)
{
    /// <summary>Проверяет и приводит значение к хранимому виду: «true» или «false» (без учёта регистра), целое без знака и пробелов в допустимых границах. null, если значение не подходит.</summary>
    public string? Normalize(string? raw)
    {
        var value = raw?.Trim() ?? string.Empty;
        if (Kind == SettingKind.Bool)
        {
            return bool.TryParse(value, out var flag) ? (flag ? "true" : "false") : null;
        }

        if (value.Length == 0 || value.Length > 9 || !value.All(char.IsAsciiDigit) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return null;
        }

        return number >= (Min ?? int.MinValue) && number <= (Max ?? int.MaxValue) ? number.ToString(CultureInfo.InvariantCulture) : null;
    }
}

/// <summary>Реестр системных настроек (раздел 5.3 и 10.4 ТЗ): единственный источник ключей, типов, границ и значений по умолчанию. Код читает настройки с этими же значениями по умолчанию.</summary>
public static class SystemSettingDefinitions
{
    public const int MaxActiveExchangesDefault = 3;
    public const int MaxPendingOutgoingDefault = 5;
    public const int MaxRequestsPerDayDefault = 10;
    public const int DeclineCooldownDaysDefault = 30;

    public static IReadOnlyList<SystemSettingDefinition> All { get; } =
    [
        new(SystemSettingKeys.MinorsMatchOnlyMinors, SettingKind.Bool, "true", null, null,
            "Подбор: пользователи 16–17 лет только друг с другом, взрослые только со взрослыми (раздел 10.4 ТЗ)."),
        new(SystemSettingKeys.MaxActiveExchanges, SettingKind.Int, MaxActiveExchangesDefault.ToString(CultureInfo.InvariantCulture), 1, 20,
            "Антиспам: не более стольких активных обменов одновременно (раздел 5.3 ТЗ)."),
        new(SystemSettingKeys.MaxPendingOutgoing, SettingKind.Int, MaxPendingOutgoingDefault.ToString(CultureInfo.InvariantCulture), 1, 50,
            "Антиспам: не более стольких ожидающих исходящих заявок (раздел 5.3 ТЗ)."),
        new(SystemSettingKeys.MaxRequestsPerDay, SettingKind.Int, MaxRequestsPerDayDefault.ToString(CultureInfo.InvariantCulture), 1, 200,
            "Антиспам: не более стольких новых заявок за сутки (раздел 5.3 ТЗ)."),
        new(SystemSettingKeys.DeclineCooldownDays, SettingKind.Int, DeclineCooldownDaysDefault.ToString(CultureInfo.InvariantCulture), 0, 365,
            "Антиспам: через сколько дней после отказа можно снова отправить заявку тому же человеку (раздел 5.3 ТЗ)."),
    ];

    public static SystemSettingDefinition? Find(string? key) => All.FirstOrDefault(d => d.Key == key);
}
