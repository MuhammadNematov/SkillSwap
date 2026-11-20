using Microsoft.Extensions.Logging;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Account;
using SkillSwap.Application.Notifications;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Moderation;

/// <summary>Что вычистил один проход очистки по обмену.</summary>
public sealed record PurgeResult(int Messages, int Attachments, IReadOnlyList<string> StorageKeys, bool Empty);

/// <summary>Выборки и массовые операции для фоновой сверки обращений и очистки чатов (раздел 10.3 ТЗ).</summary>
public interface IMaintenanceStore
{
    /// <summary>Обращения, ждущие пояснений, у которых срок ответа вышел (задача возврата в работу не сработала).</summary>
    Task<IReadOnlyList<Guid>> ListExpiredWaitingAsync(DateTime now, CancellationToken ct);

    /// <summary>Решённые обращения без апелляции, по которым прошло 7 дней (задача закрытия не сработала).</summary>
    Task<IReadOnlyList<Guid>> ListResolvedDueAsync(DateTime now, CancellationToken ct);

    /// <summary>Не взятые обращения, у которых срок реакции вышел, а отметки о просрочке ещё нет (с отслеживанием).</summary>
    Task<IReadOnlyList<Case>> ListOverdueUnmarkedAsync(DateTime now, CancellationToken ct);

    /// <summary>
    /// Действующие доступы к чату, которым пора быть отозванными (с отслеживанием): по обращению доступ нужен, только пока оно в работе
    /// (<c>InProgress</c>, <c>WaitingResponse</c>), а доступ проверяющего по апелляции — пока идёт повторная проверка (<c>AppealReview</c>).
    /// </summary>
    Task<IReadOnlyList<ChatAccessGrant>> ListStaleGrantsAsync(CancellationToken ct);

    /// <summary>Удалившиеся пользователи, чьи личные данные удержаны из-за обращения (<c>ErasurePending</c>).</summary>
    Task<IReadOnlyList<Guid>> ListErasurePendingAsync(CancellationToken ct);

    Task SaveAsync(CancellationToken ct);

    /// <summary>
    /// Отпускает сообщения-доказательства обращений, которым больше не нужна защита: отозванных и закрытых не позже чем <paramref name="closedBefore"/>
    /// (хранятся вместе с обращением год). Возвращает, сколько ссылок снято.
    /// </summary>
    Task<int> ReleaseEvidenceAsync(DateTime closedBefore, CancellationToken ct);

    /// <summary>Закрытые обмены, закрытые не позже <paramref name="closedBefore"/>, чаты которых не вычищены и по которым нет открытого обращения или апелляции.</summary>
    Task<IReadOnlyList<Guid>> ListPurgeCandidatesAsync(DateTime closedBefore, int limit, CancellationToken ct);

    /// <summary>
    /// Удаляет сообщения обмена, кроме приложенных к обращениям, их прежние версии и вложения (строки). Возвращает ключи файлов в хранилище: сами файлы удаляет вызывающий
    /// после этого. Если сообщений не осталось, отмечает обмен вычищенным.
    /// </summary>
    Task<PurgeResult> PurgeExchangeAsync(Guid exchangeId, DateTime now, CancellationToken ct);
}

/// <summary>Расписание страховочных задач (раздел 11.1 ТЗ). Регистрируется при запуске сервера фоновых задач.</summary>
public static class RecurringCatalog
{
    public const string SweepId = "cases-sweep";
    public const string ChatCleanupId = "chat-cleanup";

    /// <summary>Каждый час: сверка обращений.</summary>
    public const string SweepCron = "0 * * * *";

    /// <summary>Каждую ночь (UTC): очистка чатов.</summary>
    public const string ChatCleanupCron = "30 3 * * *";

    public static void Register(IBackgroundJobs jobs)
    {
        jobs.Recurring<CaseMaintenanceJobs>(SweepId, j => j.SweepAsync(), SweepCron);
        jobs.Recurring<ChatCleanupJobs>(ChatCleanupId, j => j.PurgeAsync(), ChatCleanupCron);
    }
}

/// <summary>
/// Страховочная сверка обращений (раздел 8.2 ТЗ). Разовые задачи (возврат в работу через 48 часов, закрытие через 7 дней) могли не выполниться, если фоновые задачи
/// были выключены в нужный момент: сверка догоняет их по состоянию. Каждый шаг идемпотентен, сбой одного обращения не мешает остальным.
/// </summary>
public sealed class CaseMaintenanceJobs
{
    private readonly IMaintenanceStore _store;
    private readonly ICaseStore _cases;
    private readonly CaseJobs _caseJobs;
    private readonly AccountDeletionService _deletion;
    private readonly IAuditLog _audit;
    private readonly INotificationPublisher _notify;
    private readonly TimeProvider _clock;
    private readonly ILogger<CaseMaintenanceJobs> _logger;

    public CaseMaintenanceJobs(
        IMaintenanceStore store, ICaseStore cases, CaseJobs caseJobs, AccountDeletionService deletion, IAuditLog audit, INotificationPublisher notify, TimeProvider clock,
        ILogger<CaseMaintenanceJobs> logger)
    {
        _store = store;
        _cases = cases;
        _caseJobs = caseJobs;
        _deletion = deletion;
        _audit = audit;
        _notify = notify;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Полная сверка: просроченное ожидание пояснений, незакрытые решённые обращения, «зависшие» доступы к чату, просроченные сроки реакции, удержанные данные.</summary>
    public async Task SweepAsync()
    {
        var now = Now();
        foreach (var id in await _store.ListExpiredWaitingAsync(now, CancellationToken.None))
        {
            await TryAsync(() => _caseJobs.ExplanationDueAsync(id), "возврат из ожидания пояснений", id);
        }

        foreach (var id in await _store.ListResolvedDueAsync(now, CancellationToken.None))
        {
            await TryAsync(() => _caseJobs.CloseIfDueAsync(id), "закрытие обращения", id);
        }

        await TryAsync(RevokeStaleGrantsAsync, "отзыв зависших доступов", null);
        await TryAsync(MarkOverdueAsync, "отметка просроченных обращений", null);
        await TryAsync(ReleaseAllHoldsAsync, "снятие удержания данных", null);
    }

    /// <summary>Обращение закрыто или отозвано: данные удалившихся сторон, удержанные из-за него, стираются, если других открытых обращений по ним нет. Идемпотентно.</summary>
    public async Task ReleaseHoldsAsync(Guid caseId)
    {
        var found = await _cases.FindAsync(caseId, tracked: false, CancellationToken.None);
        if (found is null)
        {
            return;
        }

        foreach (var userId in new[] { found.OpenedById, found.AgainstUserId })
        {
            await ReleaseHoldAsync(userId);
        }
    }

    /// <summary>
    /// Отзывает доступы, которым пора закончиться, но которые остались (решение или заключение не дошли до отзыва: сбой, отмена, перенос между состояниями).
    /// Запись в аудите без исполнителя.
    /// </summary>
    public async Task RevokeStaleGrantsAsync()
    {
        var now = Now();
        foreach (var grant in await _store.ListStaleGrantsAsync(CancellationToken.None))
        {
            if (grant.Revoke(now))
            {
                _audit.Add(new AuditEntry(null, AuditActions.ChatAccessRevoked, "Exchange", grant.ExchangeId, new { caseId = grant.CaseId, moderatorId = grant.ModeratorId, reason = "stale_grant" }));
            }
        }

        await _store.SaveAsync(CancellationToken.None);
    }

    /// <summary>Отмечает не взятые обращения, у которых вышел срок реакции (24 или 72 часа): один раз, с уведомлением админам. Взятое и повторно проверяемое не трогается.</summary>
    public async Task MarkOverdueAsync()
    {
        var now = Now();
        var overdue = await _store.ListOverdueUnmarkedAsync(now, CancellationToken.None);
        if (overdue.Count == 0)
        {
            return;
        }

        var admins = await _cases.ListActiveAdminIdsAsync(CancellationToken.None);
        foreach (var item in overdue.Where(c => c.MarkOverdue(now)))
        {
            _audit.Add(new AuditEntry(null, AuditActions.CaseOverdue, "Case", item.Id, new { priority = item.Priority, dueAt = item.DueAt }));
            foreach (var admin in admins)
            {
                _notify.Add(admin, NotificationType.CaseOverdue, new { caseId = item.Id, priority = item.Priority.ToString(), dueAt = item.DueAt });
            }
        }

        await _store.SaveAsync(CancellationToken.None);
        _notify.Dispatch();
    }

    private async Task ReleaseAllHoldsAsync()
    {
        foreach (var userId in await _store.ListErasurePendingAsync(CancellationToken.None))
        {
            await ReleaseHoldAsync(userId);
        }
    }

    private async Task ReleaseHoldAsync(Guid userId)
    {
        if (!await _cases.HasActiveCaseAsync(userId, CancellationToken.None))
        {
            await _deletion.CompleteErasureAsync(userId, CancellationToken.None);   // ничего не делает, если пользователь не ждёт стирания
        }
    }

    private async Task TryAsync(Func<Task> step, string what, Guid? caseId)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Сверка обращений: шаг «{Step}» не удался (обращение {CaseId}); остальные шаги продолжаются.", what, caseId);
        }
    }
}

/// <summary>
/// Очистка чатов (раздел 10.3 ТЗ): сообщения и вложения удаляются через 90 дней после завершения обмена, если нет открытого обращения или апелляции. Сообщения, приложенные
/// к обращению, хранятся вместе с ним год после его закрытия. Идемпотентно; сбой на одном обмене не мешает остальным.
/// </summary>
public sealed class ChatCleanupJobs
{
    /// <summary>Через сколько дней после закрытия обмена его чат удаляется.</summary>
    public static readonly TimeSpan ChatRetention = TimeSpan.FromDays(90);

    /// <summary>Сколько хранятся сообщения-доказательства после закрытия обращения.</summary>
    public static readonly TimeSpan EvidenceRetention = TimeSpan.FromDays(365);

    /// <summary>Сколько обменов обрабатывается за один запуск: остальные подхватит следующий.</summary>
    public const int BatchSize = 200;

    private readonly IMaintenanceStore _store;
    private readonly IFileStorage _storage;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChatCleanupJobs> _logger;

    public ChatCleanupJobs(IMaintenanceStore store, IFileStorage storage, IAuditLog audit, TimeProvider clock, ILogger<ChatCleanupJobs> logger)
    {
        _store = store;
        _storage = storage;
        _audit = audit;
        _clock = clock;
        _logger = logger;
    }

    public async Task PurgeAsync()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var released = await _store.ReleaseEvidenceAsync(now - EvidenceRetention, CancellationToken.None);
        if (released > 0)
        {
            await _audit.WriteAsync(new AuditEntry(null, AuditActions.EvidenceReleased, "System", null, new { links = released }), CancellationToken.None);
        }

        foreach (var exchangeId in await _store.ListPurgeCandidatesAsync(now - ChatRetention, BatchSize, CancellationToken.None))
        {
            try
            {
                var result = await _store.PurgeExchangeAsync(exchangeId, now, CancellationToken.None);
                foreach (var key in result.StorageKeys)
                {
                    await DeleteFileAsync(key);
                }

                if (result.Messages > 0 || result.Attachments > 0)
                {
                    await _audit.WriteAsync(new AuditEntry(null, AuditActions.ChatPurged, "Exchange", exchangeId, new { messages = result.Messages, attachments = result.Attachments, complete = result.Empty }), CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Очистка чата обмена {ExchangeId} не удалась; остальные обмены продолжаются.", exchangeId);
            }
        }
    }

    /// <summary>Файл удаляется после того, как строки в базе уже убраны. Если хранилище недоступно, объект остаётся сиротой (сверка бакета с таблицей — в списке отложенного).</summary>
    private async Task DeleteFileAsync(string key)
    {
        try
        {
            await _storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить файл {Key} при очистке чата.", key);
        }
    }
}
