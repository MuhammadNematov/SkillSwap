namespace SkillSwap.Application.Options;

/// <summary>
/// Ночная уборка (секция <c>Maintenance</c>, этап 7, шаг 9): очистка старых уведомлений и сверка бакета файлов с таблицей вложений. Все сроки и размеры пачек можно менять
/// конфигурацией без выкладки кода.
/// </summary>
public sealed class MaintenanceOptions
{
    public const string SectionName = "Maintenance";

    public NotificationCleanupOptions Notifications { get; set; } = new();

    public BucketReconciliationOptions BucketReconciliation { get; set; } = new();

    /// <summary>Текст ошибки, если значения недопустимы; null, если всё в порядке.</summary>
    public string? Validate()
    {
        var n = Notifications;
        if (n.ReadRetentionDays < 1 || n.UnreadRetentionDays < 1 || n.BatchSize < 1 || n.MaxBatchesPerRun < 1)
        {
            return "Maintenance:Notifications: сроки (в днях), размер пачки и число пачек за запуск не могут быть меньше 1.";
        }

        var b = BucketReconciliation;
        if (b.MinAgeHours < 1 || b.MaxOrphansPerRun < 1 || b.CheckBatchSize < 1)
        {
            return "Maintenance:BucketReconciliation: возраст файла (в часах), предел за запуск и размер пачки проверки не могут быть меньше 1.";
        }

        return null;
    }
}

/// <summary>Очистка уведомлений: прочитанные и непрочитанные хранятся по-разному. Возраст считается от времени создания (у обновлённого счётчика непрочитанных сообщений от последнего обновления).</summary>
public sealed class NotificationCleanupOptions
{
    /// <summary>Сколько дней хранятся прочитанные уведомления.</summary>
    public int ReadRetentionDays { get; set; } = 90;

    /// <summary>Сколько дней хранятся непрочитанные уведомления.</summary>
    public int UnreadRetentionDays { get; set; } = 180;

    /// <summary>Сколько уведомлений удаляется одним запросом: большие таблицы не блокируются надолго.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Сколько пачек каждого вида удаляется за один ночной запуск; остальное подхватит следующая ночь.</summary>
    public int MaxBatchesPerRun { get; set; } = 100;
}

/// <summary>Сверка бакета: файлы вложений, на которые нет строки в таблице <c>Attachments</c>, — сироты (например, загрузка оборвалась или не удалился файл после очистки чата).</summary>
public sealed class BucketReconciliationOptions
{
    /// <summary>
    /// Удалять ли найденные файлы-сироты. <c>false</c> (по умолчанию): только отчёт в журнал аудита и логи, ничего не удаляется. Первый запуск на боевой среде делается именно так:
    /// сверить отчёт с ожиданиями, потом включить удаление (<c>Maintenance__BucketReconciliation__DeleteOrphans=true</c>).
    /// </summary>
    public bool DeleteOrphans { get; set; }

    /// <summary>Файлы моложе этого возраста не трогаются: загрузка кладёт файл раньше, чем появляется строка в базе.</summary>
    public int MinAgeHours { get; set; } = 24;

    /// <summary>Сколько сирот обрабатывается за один запуск (в отчёте или с удалением); дальше сверка останавливается, остальное подхватит следующая ночь.</summary>
    public int MaxOrphansPerRun { get; set; } = 1000;

    /// <summary>Сколько ключей проверяется по базе одним запросом.</summary>
    public int CheckBatchSize { get; set; } = 500;
}
