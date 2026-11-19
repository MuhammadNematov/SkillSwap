using System.Net;
using Hangfire;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Abstractions;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Jobs;

/// <summary>Настоящий Hangfire: сервер очереди в PostgreSQL выполняет, откладывает и повторяет задачи.</summary>
[Collection(ApiCollection.Name)]
public class HangfireRuntimeTests
{
    public sealed class JobSignals
    {
        private readonly List<string> _events = [];
        private readonly object _gate = new();

        public void Add(string value)
        {
            lock (_gate)
            {
                _events.Add(value);
            }
        }

        public List<string> Snapshot()
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }

        public async Task<List<string>> WaitForAsync(int count, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(25));
            while (Snapshot().Count < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            return Snapshot();
        }
    }

    public sealed class RuntimeProbe(JobSignals signals)
    {
        public Task RunAsync(string text)
        {
            signals.Add("run:" + text);
            return Task.CompletedTask;
        }

        [AutomaticRetry(Attempts = 2, DelaysInSeconds = [1])]
        public Task FailOnceAsync(string key)
        {
            var attempts = signals.Snapshot().Count(e => e == "try:" + key) + 1;
            signals.Add("try:" + key);
            return attempts == 1 ? throw new InvalidOperationException("первая попытка падает") : Task.CompletedTask;
        }
    }

    private readonly ApiFactory _factory;

    public HangfireRuntimeTests(ApiFactory factory) => _factory = factory;

    private WebApplicationFactory<Program> Enabled(string? environment = null) =>
        _factory.WithSettings(environment, ("Hangfire:Enabled", "true"), ("Hangfire:QueuePollIntervalSeconds", "1"))
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<JobSignals>().AddScoped<RuntimeProbe>()));

    [Fact]
    public async Task The_server_runs_an_enqueued_job_with_its_arguments()
    {
        using var app = Enabled();
        var jobs = app.Services.GetRequiredService<IBackgroundJobs>();
        Assert.IsType<HangfireBackgroundJobs>(jobs);

        jobs.Enqueue<RuntimeProbe>(p => p.RunAsync("привет"));

        Assert.Equal(["run:привет"], await app.Services.GetRequiredService<JobSignals>().WaitForAsync(1));
    }

    [Fact]
    public async Task A_scheduled_job_waits_for_its_time()
    {
        using var app = Enabled();
        var jobs = app.Services.GetRequiredService<IBackgroundJobs>();
        var signals = app.Services.GetRequiredService<JobSignals>();

        jobs.Schedule<RuntimeProbe>(p => p.RunAsync("позже"), DateTimeOffset.UtcNow.AddSeconds(3));

        await Task.Delay(800);
        Assert.Empty(signals.Snapshot());                         // рано
        Assert.Equal(["run:позже"], await signals.WaitForAsync(1));
    }

    [Fact]
    public async Task A_failing_job_is_retried_by_the_server()
    {
        using var app = Enabled();
        var jobs = app.Services.GetRequiredService<IBackgroundJobs>();
        var signals = app.Services.GetRequiredService<JobSignals>();

        jobs.Enqueue<RuntimeProbe>(p => p.FailOnceAsync("k1"));

        Assert.Equal(["try:k1", "try:k1"], await signals.WaitForAsync(2));   // вторая попытка прошла
    }

    [Fact]
    public async Task The_queue_lives_in_its_own_schema_of_the_application_database()
    {
        using var app = Enabled();
        _ = app.Services.GetRequiredService<IBackgroundJobs>();
        app.Services.GetRequiredService<IBackgroundJobs>().Enqueue<RuntimeProbe>(p => p.RunAsync("схема"));
        await app.Services.GetRequiredService<JobSignals>().WaitForAsync(1);

        var tables = await _factory.QueryAsync(async db =>
            await db.Database.SqlQueryRaw<string>("SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'hangfire'").ToListAsync());

        Assert.Contains("job", tables);
        Assert.Contains("jobqueue", tables);
    }

    [Fact]
    public void With_hangfire_switched_off_jobs_run_inline_and_the_queue_is_never_touched()
    {
        using var app = _factory.WithSettings();   // Hangfire:Enabled=false из настроек тестов

        Assert.IsType<InlineBackgroundJobs>(app.Services.GetRequiredService<IBackgroundJobs>());
    }

    [Theory]
    [InlineData("Development", true)]    // панель только в разработке
    [InlineData("Testing", false)]
    [InlineData("Production", false)]    // на боевом сервере панели нет
    public async Task The_dashboard_exists_only_in_development(string environment, bool exists)
    {
        using var app = Enabled(environment);

        var response = await app.CreateClient().GetAsync("/hangfire");

        Assert.Equal(exists, response.StatusCode == HttpStatusCode.OK);
        if (!exists)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_dashboard_is_not_there_when_hangfire_is_off_even_in_development()
    {
        using var app = _factory.WithSettings("Development");

        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync("/hangfire")).StatusCode);
    }
}
