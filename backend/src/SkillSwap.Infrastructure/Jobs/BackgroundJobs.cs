using System.Linq.Expressions;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkillSwap.Application.Abstractions;

namespace SkillSwap.Infrastructure.Jobs;

/// <summary>Фоновые задачи через Hangfire: вызов сохраняется в PostgreSQL и выполняется сервером Hangfire, при сбое повторяется.</summary>
public sealed class HangfireBackgroundJobs : IBackgroundJobs
{
    private readonly IBackgroundJobClient _client;

    public HangfireBackgroundJobs(IBackgroundJobClient client)
    {
        _client = client;
    }

    public void Enqueue<T>(Expression<Func<T, Task>> job) where T : notnull => _client.Enqueue(job);

    public void Schedule<T>(Expression<Func<T, Task>> job, DateTimeOffset runAt) where T : notnull => _client.Schedule(job, runAt);

    public void Recurring<T>(string id, Expression<Func<T, Task>> job, string cron) where T : notnull =>
        RecurringJob.AddOrUpdate(id, job, cron, new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
}

/// <summary>
/// Выполняет задачи сразу, в том же процессе, без Hangfire и без БД (<c>Hangfire:Enabled=false</c>). Нужна тестам: письма и
/// другие последствия видны сразу после запроса, ждать очередь не нужно. Отложенные задачи не запускаются сами, их запускает
/// тест через <see cref="RunDueAsync"/> (так время задач управляется часами теста, а не настоящим временем).
/// Сбой задачи не ломает вызвавший запрос (как и в Hangfire): он записывается в <see cref="Failures"/> и журнал.
/// </summary>
public sealed class InlineBackgroundJobs : IBackgroundJobs
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InlineBackgroundJobs> _logger;
    private readonly object _gate = new();
    private readonly List<ScheduledJob> _scheduled = [];
    private readonly List<Exception> _failures = [];

    public InlineBackgroundJobs(IServiceScopeFactory scopes, ILogger<InlineBackgroundJobs> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public sealed record ScheduledJob(DateTimeOffset RunAt, Func<IServiceScopeFactory, Task> Run, string Description);

    public IReadOnlyList<ScheduledJob> Scheduled
    {
        get { lock (_gate) { return [.. _scheduled]; } }
    }

    public IReadOnlyList<Exception> Failures
    {
        get { lock (_gate) { return [.. _failures]; } }
    }

    private readonly Dictionary<string, (string Cron, Func<IServiceScopeFactory, Task> Run)> _recurring = [];

    /// <summary>Зарегистрированные повторяющиеся задачи (идентификатор и расписание); сами не запускаются, тест вызывает <see cref="RunRecurringAsync"/>.</summary>
    public IReadOnlyDictionary<string, string> RecurringJobs
    {
        get { lock (_gate) { return _recurring.ToDictionary(p => p.Key, p => p.Value.Cron); } }
    }

    public void Recurring<T>(string id, Expression<Func<T, Task>> job, string cron) where T : notnull
    {
        lock (_gate)
        {
            _recurring[id] = (cron, Build(job));
        }
    }

    /// <summary>Выполняет повторяющуюся задачу один раз (как это сделал бы планировщик).</summary>
    public async Task RunRecurringAsync(string id)
    {
        Func<IServiceScopeFactory, Task> run;
        lock (_gate)
        {
            run = _recurring[id].Run;
        }

        await RunSafelyAsync(run);
    }

    public void Enqueue<T>(Expression<Func<T, Task>> job) where T : notnull => RunNow(Build(job));

    public void Schedule<T>(Expression<Func<T, Task>> job, DateTimeOffset runAt) where T : notnull
    {
        lock (_gate)
        {
            _scheduled.Add(new ScheduledJob(runAt, Build(job), job.ToString()));
        }
    }

    /// <summary>Запускает отложенные задачи, время которых наступило к <paramref name="now"/>, и убирает их из очереди.</summary>
    public async Task<int> RunDueAsync(DateTimeOffset now)
    {
        List<ScheduledJob> due;
        lock (_gate)
        {
            due = _scheduled.Where(s => s.RunAt <= now).OrderBy(s => s.RunAt).ToList();
            _scheduled.RemoveAll(due.Contains);
        }

        foreach (var item in due)
        {
            await RunSafelyAsync(item.Run);
        }

        return due.Count;
    }

    private static Func<IServiceScopeFactory, Task> Build<T>(Expression<Func<T, Task>> job) where T : notnull
    {
        var compiled = job.Compile();   // аргументы вычисляются сейчас, как и в Hangfire (там они сохраняются в БД)
        return async scopes =>
        {
            using var scope = scopes.CreateScope();
            await compiled(scope.ServiceProvider.GetRequiredService<T>());
        };
    }

    private void RunNow(Func<IServiceScopeFactory, Task> run) => RunSafelyAsync(run).GetAwaiter().GetResult();

    private async Task RunSafelyAsync(Func<IServiceScopeFactory, Task> run)
    {
        try
        {
            await run(_scopes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Фоновая задача завершилась ошибкой (встроенный исполнитель)");
            lock (_gate)
            {
                _failures.Add(ex);
            }
        }
    }
}
