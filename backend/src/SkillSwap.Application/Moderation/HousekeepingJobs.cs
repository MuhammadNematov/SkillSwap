using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Moderation;

/// <summary>
/// Очистка старых уведомлений (этап 7, шаг 9): прочитанные хранятся 90 дней, непрочитанные 180 (сроки в конфигурации <c>Maintenance:Notifications</c>). Ночная задача удаляет
/// пачками, чтобы большая таблица не блокировалась надолго; за запуск не больше <c>MaxBatchesPerRun</c> пачек каждого вида, остальное доберёт следующая ночь.
/// Идемпотентна. Сбой не ломает остальные задачи: ошибка пишется в лог, а то, что уже удалено, остаётся удалённым.
/// </summary>
public sealed class NotificationCleanupJobs
{
    private readonly IMaintenanceStore _store;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;
    private readonly IOptions<MaintenanceOptions> _options;
    private readonly ILogger<NotificationCleanupJobs> _logger;

    public NotificationCleanupJobs(IMaintenanceStore store, IAuditLog audit, TimeProvider clock, IOptions<MaintenanceOptions> options, ILogger<NotificationCleanupJobs> logger)
    {
        _store = store;
        _audit = audit;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task PurgeAsync()
    {
        var o = _options.Value.Notifications;
        var now = _clock.GetUtcNow().UtcDateTime;
        int read = 0, unread = 0;
        try
        {
            read = await DeleteInBatchesAsync(true, now - TimeSpan.FromDays(o.ReadRetentionDays), o);
            unread = await DeleteInBatchesAsync(false, now - TimeSpan.FromDays(o.UnreadRetentionDays), o);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Очистка уведомлений прервана ошибкой; уже удалённое остаётся удалённым, остальное доберёт следующий запуск.");
        }

        if (read + unread > 0)
        {
            await _audit.WriteAsync(new AuditEntry(null, AuditActions.NotificationsPurged, "System", null, new { read, unread, readRetentionDays = o.ReadRetentionDays, unreadRetentionDays = o.UnreadRetentionDays }), CancellationToken.None);
        }
    }

    private async Task<int> DeleteInBatchesAsync(bool read, DateTime olderThan, NotificationCleanupOptions o)
    {
        var total = 0;
        for (var batch = 0; batch < o.MaxBatchesPerRun; batch++)
        {
            var deleted = await _store.DeleteOldNotificationsAsync(read, olderThan, o.BatchSize, CancellationToken.None);
            total += deleted;
            if (deleted < o.BatchSize)
            {
                break;
            }
        }

        return total;
    }
}

/// <summary>
/// Сверка бакета с таблицей вложений (этап 7, шаг 9). Файлы вложений (ключи <c>exchanges/…</c>), на которые нет строки в <c>Attachments</c> и которым больше суток (загрузка кладёт файл
/// раньше строки), считаются сиротами: оборванная загрузка или файл, который не удалился после очистки чата. С <c>Maintenance:BucketReconciliation:DeleteOrphans=false</c> (по умолчанию)
/// задача только пишет отчёт в журнал аудита и лог и **ничего не удаляет**: так делается первый запуск на боевой среде. С <c>true</c> сироты удаляются, не больше
/// <c>MaxOrphansPerRun</c> за запуск. Аватары и выгрузки данных не сверяются (у них свои таблицы и свои сроки). Если хранилище недоступно, сверка пропускается без удаления.
/// </summary>
public sealed class BucketReconciliationJobs
{
    /// <summary>Общий префикс ключей вложений (<see cref="Attachment.KeyFor"/>).</summary>
    public const string AttachmentPrefix = "exchanges/";

    /// <summary>Сколько ключей-сирот попадает в запись аудита (полный список в логе).</summary>
    public const int ReportSample = 50;

    private readonly IMaintenanceStore _store;
    private readonly IFileStorage _storage;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;
    private readonly IOptions<MaintenanceOptions> _options;
    private readonly ILogger<BucketReconciliationJobs> _logger;

    public BucketReconciliationJobs(
        IMaintenanceStore store, IFileStorage storage, IAuditLog audit, TimeProvider clock, IOptions<MaintenanceOptions> options, ILogger<BucketReconciliationJobs> logger)
    {
        _store = store;
        _storage = storage;
        _audit = audit;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task ReconcileAsync()
    {
        var o = _options.Value.BucketReconciliation;
        var cutoff = _clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(o.MinAgeHours);
        var scanned = 0;
        var tooYoung = 0;
        var orphans = 0;
        var deleted = 0;
        var failed = 0;
        long orphanBytes = 0;
        var truncated = false;
        var sample = new List<string>();
        var batch = new List<StoredObject>();

        async Task FlushAsync()
        {
            if (batch.Count == 0)
            {
                return;
            }

            var known = await _store.FindAttachmentKeysAsync(batch.Select(b => b.Key).ToList(), CancellationToken.None);
            foreach (var item in batch.Where(b => !known.Contains(b.Key)))
            {
                if (orphans >= o.MaxOrphansPerRun)
                {
                    truncated = true;
                    break;
                }

                orphans++;
                orphanBytes += item.Size;
                if (sample.Count < ReportSample)
                {
                    sample.Add(item.Key);
                }

                if (!o.DeleteOrphans)
                {
                    _logger.LogWarning("Сверка бакета (только отчёт): файл {Key} без строки в Attachments ({Size} байт, изменён {Modified:O}).", item.Key, item.Size, item.LastModifiedUtc);
                    continue;
                }

                try
                {
                    await _storage.DeleteAsync(item.Key, CancellationToken.None);
                    deleted++;
                    _logger.LogInformation("Сверка бакета: удалён файл-сирота {Key} ({Size} байт).", item.Key, item.Size);
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex, "Сверка бакета: не удалось удалить файл {Key}.", item.Key);
                }
            }

            batch.Clear();
        }

        try
        {
            await foreach (var item in _storage.ListAsync(AttachmentPrefix, CancellationToken.None))
            {
                scanned++;
                if (item.LastModifiedUtc > cutoff)
                {
                    tooYoung++;
                    continue;
                }

                batch.Add(item);
                if (batch.Count >= o.CheckBatchSize)
                {
                    await FlushAsync();
                }

                if (truncated || orphans >= o.MaxOrphansPerRun)
                {
                    truncated = true;
                    break;
                }
            }

            await FlushAsync();
        }
        catch (Exception ex)
        {
            // Хранилище или база недоступны: перечисление неполное, поэтому отчёт не пишется как «всё сверено»; удалённое остаётся удалённым.
            _logger.LogError(ex, "Сверка бакета прервана ошибкой; остальное доберёт следующий запуск.");
            return;
        }

        _logger.LogInformation(
            "Сверка бакета ({Mode}): просмотрено {Scanned}, моложе {Hours} ч {Young}, сирот {Orphans}, удалено {Deleted}, не удалось {Failed}{Truncated}.",
            o.DeleteOrphans ? "с удалением" : "только отчёт", scanned, o.MinAgeHours, tooYoung, orphans, deleted, failed, truncated ? ", достигнут предел за запуск" : string.Empty);
        await _audit.WriteAsync(new AuditEntry(null, AuditActions.BucketReconciled, "System", null, new
        {
            mode = o.DeleteOrphans ? "delete" : "report",
            scanned,
            tooYoung,
            orphans,
            orphanBytes,
            deleted,
            failed,
            truncated,
            minAgeHours = o.MinAgeHours,
            sample,
        }), CancellationToken.None);
    }
}
