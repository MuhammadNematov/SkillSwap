using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Api.IntegrationTests.Staff;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;
using SkillSwap.Infrastructure.Storage;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>Ночная уборка (этап 7, шаг 9): очистка старых уведомлений пачками и сверка бакета с таблицей вложений (отчёт без удаления и удаление).</summary>
[Collection(ApiCollection.Name)]
public class HousekeepingTests
{
    private readonly ApiFactory _factory;

    public HousekeepingTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private InlineBackgroundJobs Jobs => (InlineBackgroundJobs)_factory.Services.GetRequiredService<IBackgroundJobs>();

    // ======================= уведомления =======================

    private NotificationCleanupJobs CleanupJobs(IServiceScope scope, MaintenanceOptions options) => new(
        scope.ServiceProvider.GetRequiredService<IMaintenanceStore>(), scope.ServiceProvider.GetRequiredService<IAuditLog>(), _factory.Clock, Options.Create(options),
        NullLogger<NotificationCleanupJobs>.Instance);

    private async Task RunCleanupAsync(MaintenanceOptions? options = null)
    {
        using var scope = _factory.Services.CreateScope();
        await CleanupJobs(scope, options ?? new MaintenanceOptions()).PurgeAsync();
    }

    private async Task<Guid> NotificationAsync(Guid userId, bool read, double ageDays)
    {
        var notification = Notification.Create(userId, NotificationType.CaseTaken, "{}");
        if (read)
        {
            notification.MarkRead(Now);
        }

        await _factory.QueryAsync(async db =>
        {
            db.Notifications.Add(notification);
            await db.SaveChangesAsync();
            await db.Notifications.Where(n => n.Id == notification.Id).ExecuteUpdateAsync(s => s.SetProperty(n => n.CreatedAt, Now.AddDays(-ageDays)));
            return 0;
        });
        return notification.Id;
    }

    private Task<HashSet<Guid>> ExistingAsync(params Guid[] ids) =>
        _factory.QueryAsync(async db => (await db.Notifications.AsNoTracking().Where(n => ids.Contains(n.Id)).Select(n => n.Id).ToListAsync()).ToHashSet());

    [Fact]
    public async Task Read_notifications_older_than_90_days_and_unread_older_than_180_are_deleted_and_the_rest_stay()
    {
        var (user, _) = await StaffActor.PersonalAsync(_factory);
        var oldRead = await NotificationAsync(user.Id, read: true, ageDays: 91);
        var freshRead = await NotificationAsync(user.Id, read: true, ageDays: 89);
        var oldUnread = await NotificationAsync(user.Id, read: false, ageDays: 181);
        var midUnread = await NotificationAsync(user.Id, read: false, ageDays: 100);   // непрочитанное хранится дольше прочитанного
        var freshUnread = await NotificationAsync(user.Id, read: false, ageDays: 1);

        await RunCleanupAsync();

        var left = await ExistingAsync(oldRead, freshRead, oldUnread, midUnread, freshUnread);
        Assert.DoesNotContain(oldRead, left);
        Assert.DoesNotContain(oldUnread, left);
        Assert.Contains(freshRead, left);
        Assert.Contains(midUnread, left);
        Assert.Contains(freshUnread, left);
    }

    [Fact]
    public async Task The_retention_terms_come_from_the_configuration()
    {
        var (user, _) = await StaffActor.PersonalAsync(_factory);
        var read = await NotificationAsync(user.Id, read: true, ageDays: 10);
        var unread = await NotificationAsync(user.Id, read: false, ageDays: 20);
        var young = await NotificationAsync(user.Id, read: true, ageDays: 2);

        await RunCleanupAsync(new MaintenanceOptions { Notifications = { ReadRetentionDays = 5, UnreadRetentionDays = 15 } });

        var left = await ExistingAsync(read, unread, young);
        Assert.Equal([young], left);
    }

    [Fact]
    public async Task Deletion_goes_in_batches_and_a_run_stops_at_the_batch_limit()
    {
        var (user, _) = await StaffActor.PersonalAsync(_factory);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await NotificationAsync(user.Id, read: true, ageDays: 5000 + i));   // старше всего в общей базе: удаляются первыми
        }

        await RunCleanupAsync(new MaintenanceOptions { Notifications = { BatchSize = 2, MaxBatchesPerRun = 2 } });
        Assert.Single(await ExistingAsync([.. ids]));   // 2 пачки по 2: пять без четырёх

        await RunCleanupAsync(new MaintenanceOptions { Notifications = { BatchSize = 2, MaxBatchesPerRun = 10 } });
        Assert.Empty(await ExistingAsync([.. ids]));    // следующий запуск доедает остаток
    }

    [Fact]
    public async Task A_cleanup_that_deleted_something_is_in_the_audit_log_and_one_that_did_not_is_silent()
    {
        var (user, _) = await StaffActor.PersonalAsync(_factory);
        await NotificationAsync(user.Id, read: true, ageDays: 400);
        var before = await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == "NotificationsPurged"));

        await RunCleanupAsync();
        var afterFirst = await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == "NotificationsPurged"));
        await RunCleanupAsync();
        var afterSecond = await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == "NotificationsPurged"));

        Assert.Equal(1, afterFirst - before);
        Assert.Equal(afterFirst, afterSecond);   // повторный запуск ничего не нашёл и ничего не записал
    }

    [Fact]
    public void Wrong_maintenance_options_are_reported()
    {
        Assert.Null(new MaintenanceOptions().Validate());
        Assert.NotNull(new MaintenanceOptions { Notifications = { ReadRetentionDays = 0 } }.Validate());
        Assert.NotNull(new MaintenanceOptions { Notifications = { BatchSize = 0 } }.Validate());
        Assert.NotNull(new MaintenanceOptions { BucketReconciliation = { MinAgeHours = 0 } }.Validate());
        Assert.NotNull(new MaintenanceOptions { BucketReconciliation = { MaxOrphansPerRun = 0 } }.Validate());
        Assert.Equal(90, new MaintenanceOptions().Notifications.ReadRetentionDays);
        Assert.Equal(180, new MaintenanceOptions().Notifications.UnreadRetentionDays);
        Assert.False(new MaintenanceOptions().BucketReconciliation.DeleteOrphans);   // по умолчанию только отчёт
        Assert.Equal(24, new MaintenanceOptions().BucketReconciliation.MinAgeHours);
    }

    // ======================= сверка бакета =======================

    /// <summary>Сверка с часами, убежавшими вперёд: свежезагруженные файлы выглядят «старыми» (настоящее время файла в хранилище задать нельзя).</summary>
    private async Task<JsonElement?> RunReconcileAsync(BucketReconciliationOptions options, TimeSpan clockAhead, IFileStorage? storage = null)
    {
        var before = (await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action == "BucketReconciled").Select(a => a.Id).ToListAsync())).ToHashSet();
        using var scope = _factory.Services.CreateScope();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow + clockAhead);
        var job = new BucketReconciliationJobs(
            scope.ServiceProvider.GetRequiredService<IMaintenanceStore>(), storage ?? scope.ServiceProvider.GetRequiredService<IFileStorage>(),
            scope.ServiceProvider.GetRequiredService<IAuditLog>(), clock, Options.Create(new MaintenanceOptions { BucketReconciliation = options }),
            NullLogger<BucketReconciliationJobs>.Instance);
        await job.ReconcileAsync();

        // Время записи аудита идёт по часам приложения (замороженным), поэтому свой отчёт отличаем по новой записи, а не по порядку времени.
        var added = await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action == "BucketReconciled" && !before.Contains(a.Id)).ToListAsync());
        return added.Count == 0 ? null : JsonDocument.Parse(added.Single().DetailsJson).RootElement;
    }

    private static string OrphanKey() => $"exchanges/{Guid.NewGuid():N}/{Guid.NewGuid():N}";

    private async Task PutAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFileStorage>().PutAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("orphan")), "text/plain", CancellationToken.None);
    }

    private async Task<bool> ExistsAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IFileStorage>().GetAsync(key, CancellationToken.None);
        stored?.Content.Dispose();
        return stored is not null;
    }

    /// <summary>Вложение с настоящим файлом: строка в базе и объект в бакете.</summary>
    private async Task<string> KnownFileAsync()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _factory.CreateClient());
        var attachment = Attachment.Create(x.A.Id, x.ExchangeId, AttachmentKind.File, "notes.txt", "text/plain", 6, null);
        await _factory.QueryAsync(async db =>
        {
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync();
            return 0;
        });
        await PutAsync(attachment.StorageKey);
        return attachment.StorageKey;
    }

    private static bool Listed(JsonElement report, string key) => report.GetProperty("sample").EnumerateArray().Any(s => s.GetString() == key);

    [Fact]
    public async Task By_default_the_reconciliation_only_reports_the_orphans_and_deletes_nothing()
    {
        var orphan = OrphanKey();
        await PutAsync(orphan);
        var known = await KnownFileAsync();

        var report = (await RunReconcileAsync(new BucketReconciliationOptions { MaxOrphansPerRun = 1_000_000 }, TimeSpan.FromHours(30)))!.Value;

        Assert.True(await ExistsAsync(orphan));
        Assert.True(await ExistsAsync(known));
        Assert.Equal("report", report.GetProperty("mode").GetString());
        Assert.True(report.GetProperty("orphans").GetInt32() >= 1);
        Assert.Equal(0, report.GetProperty("deleted").GetInt32());
        Assert.True(Listed(report, orphan) || report.GetProperty("orphans").GetInt32() > BucketReconciliationJobs.ReportSample);
        Assert.False(Listed(report, known));   // файл со строкой в Attachments сиротой не считается
    }

    [Fact]
    public async Task With_the_flag_on_orphans_older_than_a_day_are_deleted_and_files_with_a_row_stay()
    {
        var orphan = OrphanKey();
        var another = OrphanKey();
        await PutAsync(orphan);
        await PutAsync(another);
        var known = await KnownFileAsync();

        var report = (await RunReconcileAsync(new BucketReconciliationOptions { DeleteOrphans = true, MaxOrphansPerRun = 1_000_000 }, TimeSpan.FromHours(30)))!.Value;

        Assert.False(await ExistsAsync(orphan));
        Assert.False(await ExistsAsync(another));
        Assert.True(await ExistsAsync(known));
        Assert.Equal("delete", report.GetProperty("mode").GetString());
        Assert.True(report.GetProperty("deleted").GetInt32() >= 2);
        Assert.Equal(0, report.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task A_file_younger_than_a_day_is_not_touched_even_with_deletion_on()
    {
        var fresh = OrphanKey();
        await PutAsync(fresh);   // загружен только что: строка в базе могла просто ещё не появиться

        var report = (await RunReconcileAsync(new BucketReconciliationOptions { DeleteOrphans = true }, TimeSpan.FromHours(2)))!.Value;

        Assert.True(await ExistsAsync(fresh));
        Assert.True(report.GetProperty("tooYoung").GetInt32() >= 1);
        await DeleteAsync(fresh);
    }

    [Fact]
    public async Task A_run_deletes_no_more_than_the_limit_and_says_it_was_cut_short()
    {
        var keys = new[] { OrphanKey(), OrphanKey(), OrphanKey() };
        foreach (var key in keys)
        {
            await PutAsync(key);
        }

        var report = (await RunReconcileAsync(new BucketReconciliationOptions { DeleteOrphans = true, MaxOrphansPerRun = 2, CheckBatchSize = 2 }, TimeSpan.FromHours(30)))!.Value;

        Assert.Equal(2, report.GetProperty("deleted").GetInt32());
        Assert.True(report.GetProperty("truncated").GetBoolean());
        Assert.True((await Task.WhenAll(keys.Select(ExistsAsync))).Count(e => e) >= 1);   // хотя бы часть осталась на следующий запуск
        foreach (var key in keys)
        {
            await DeleteAsync(key);
        }
    }

    [Fact]
    public async Task Files_of_other_kinds_are_not_checked()
    {
        var avatar = $"avatars/{Guid.NewGuid():N}/{Guid.NewGuid():N}.webp";
        await PutAsync(avatar);

        await RunReconcileAsync(new BucketReconciliationOptions { DeleteOrphans = true, MaxOrphansPerRun = 1_000_000 }, TimeSpan.FromHours(30));

        Assert.True(await ExistsAsync(avatar));   // у аватаров своя таблица и свои сроки
        await DeleteAsync(avatar);
    }

    [Fact]
    public async Task When_the_storage_is_unavailable_nothing_is_deleted_and_nothing_is_reported()
    {
        var report = await RunReconcileAsync(new BucketReconciliationOptions { DeleteOrphans = true }, TimeSpan.FromHours(30), new UnavailableFileStorage());   // не бросает

        Assert.Null(report);
    }

    [Fact]
    public async Task The_nightly_jobs_are_registered_after_the_chat_cleanup_and_run_without_failures()
    {
        RecurringCatalog.Register(Jobs);

        var registered = Jobs.RecurringJobs;
        Assert.Equal(RecurringCatalog.NotificationCleanupCron, registered[RecurringCatalog.NotificationCleanupId]);
        Assert.Equal(RecurringCatalog.BucketReconciliationCron, registered[RecurringCatalog.BucketReconciliationId]);
        var ordered = new[] { RecurringCatalog.ChatCleanupCron, RecurringCatalog.NotificationCleanupCron, RecurringCatalog.BucketReconciliationCron };
        Assert.Equal(ordered, ordered.OrderBy(c => TimeSpan.Parse($"{c.Split(' ')[1]}:{c.Split(' ')[0]}:00")));   // 03:30, 04:15, 04:45

        var failures = Jobs.Failures.Count;
        await Jobs.RunRecurringAsync(RecurringCatalog.NotificationCleanupId);
        await Jobs.RunRecurringAsync(RecurringCatalog.BucketReconciliationId);
        Assert.Equal(failures, Jobs.Failures.Count);
    }

    private async Task DeleteAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFileStorage>().DeleteAsync(key, CancellationToken.None);
    }
}
