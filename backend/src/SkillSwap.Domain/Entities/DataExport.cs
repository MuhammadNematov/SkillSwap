using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Выгрузка данных пользователя (раздел 8.1 ТЗ): фоновая задача собирает JSON, кладёт его в закрытый бакет, на email уходит ссылка на 24 часа,
/// потом файл удаляется. Запрашивать можно не чаще раза в сутки (<see cref="MinInterval"/>), неудавшаяся выгрузка не мешает повторить.
/// </summary>
public class DataExport : BaseEntity
{
    /// <summary>Минимальный интервал между запросами выгрузки.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(24);

    /// <summary>Сколько действует ссылка на скачивание.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(24);

    private DataExport()
    {
    }

    public Guid UserId { get; private set; }
    public DataExportStatus Status { get; private set; }
    public string? StorageKey { get; private set; }
    public long? SizeBytes { get; private set; }
    public DateTime? ReadyAt { get; private set; }
    public DateTime? ExpiresAt { get; private set; }

    public static DataExport Request(Guid userId) => new() { UserId = userId, Status = DataExportStatus.Pending };

    public static string KeyFor(Guid userId, Guid exportId) => $"exports/{userId:N}/{exportId:N}.json";

    /// <summary>Задача взяла выгрузку в работу. Повторный запуск уже начатой выгрузки ничего не делает (false).</summary>
    public bool Start()
    {
        if (Status != DataExportStatus.Pending)
        {
            return false;
        }

        Status = DataExportStatus.Processing;
        return true;
    }

    public void Complete(long sizeBytes, DateTime now)
    {
        Status = DataExportStatus.Ready;
        StorageKey = KeyFor(UserId, Id);
        SizeBytes = sizeBytes;
        ReadyAt = now;
        ExpiresAt = now + LinkLifetime;
    }

    public void Fail() => Status = DataExportStatus.Failed;

    /// <summary>Срок ссылки вышел (фоновая задача): файл удаляют, статус Expired.</summary>
    public bool ExpireIfDue(DateTime now)
    {
        if (Status != DataExportStatus.Ready || now < ExpiresAt)
        {
            return false;
        }

        Status = DataExportStatus.Expired;
        return true;
    }
}
