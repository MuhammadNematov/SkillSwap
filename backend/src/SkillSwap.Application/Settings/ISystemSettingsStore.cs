namespace SkillSwap.Application.Settings;

public interface ISystemSettingsStore
{
    /// <summary>Значение настройки или null, если её нет.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct);
}

public static class SystemSettingsExtensions
{
    /// <summary>Булева настройка. Нет настройки или значение не «true»/«false» — берётся значение по умолчанию.</summary>
    public static async Task<bool> GetBoolAsync(this ISystemSettingsStore store, string key, bool defaultValue, CancellationToken ct) =>
        bool.TryParse(await store.GetAsync(key, ct), out var value) ? value : defaultValue;

    /// <summary>Целая настройка. Нет настройки или значение не число — берётся значение по умолчанию.</summary>
    public static async Task<int> GetIntAsync(this ISystemSettingsStore store, string key, int defaultValue, CancellationToken ct) =>
        int.TryParse(await store.GetAsync(key, ct), out var value) ? value : defaultValue;
}
