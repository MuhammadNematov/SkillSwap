using System.Net.Http.Json;
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

    // ======================= панель для суперадмина =======================

    private static async Task<HttpResponseMessage> OpenPanelAsync(HttpClient client, string token, object? confirmation)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/superadmin/hangfire/session") { Content = System.Net.Http.Json.JsonContent.Create(new { confirmation }) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static string CookieFrom(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("skillswap_hangfire=", StringComparison.Ordinal));

    [Theory]
    [InlineData("Development")]   // панель одинаково защищена во всех средах
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task The_panel_is_closed_without_a_session_in_every_environment(string environment)
    {
        using var app = Enabled(environment);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var bare = await client.GetAsync("/hangfire");
        var fakeTicket = await client.GetAsync("/hangfire?ticket=not-a-real-ticket");
        var fakeCookie = new HttpRequestMessage(HttpMethod.Get, "/hangfire");
        fakeCookie.Headers.Add("Cookie", "skillswap_hangfire=not-a-real-session");

        Assert.Equal(HttpStatusCode.Unauthorized, bare.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, fakeTicket.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(fakeCookie)).StatusCode);
    }

    [Fact]
    public async Task A_superadmin_opens_the_panel_with_a_one_time_ticket_and_then_works_by_a_cookie_session()
    {
        using var app = Enabled();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var superAdmin = await SkillSwap.Api.IntegrationTests.Staff.StaffActor.CreateAsync(_factory, SkillSwap.Domain.Enums.UserRole.SuperAdmin);

        var opened = await OpenPanelAsync(client, superAdmin.Token, superAdmin.Confirm());

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var dto = await opened.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var url = dto.GetProperty("url").GetString()!;
        Assert.StartsWith("/hangfire?ticket=", url);
        Assert.Equal(30, dto.GetProperty("sessionMinutes").GetInt32());

        var enter = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, enter.StatusCode);
        var cookie = CookieFrom(enter);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/hangfire", cookie, StringComparison.OrdinalIgnoreCase);
        var session = cookie.Split(';')[0];

        var viaCookie = new HttpRequestMessage(HttpMethod.Get, "/hangfire");
        viaCookie.Headers.Add("Cookie", session);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(viaCookie)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);   // билет одноразовый
        Assert.Equal(1, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == "HangfirePanelOpened" && a.ActorId == superAdmin.Id)));

        // Снятого суперадмина панель перестаёт пускать сразу, хотя сессия ещё не истекла.
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == superAdmin.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Status, SkillSwap.Domain.Enums.UserStatus.Blocked));
            return 0;
        });
        var afterRemoval = new HttpRequestMessage(HttpMethod.Get, "/hangfire");
        afterRemoval.Headers.Add("Cookie", session);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(afterRemoval)).StatusCode);
    }

    [Fact]
    public async Task The_ticket_is_given_only_to_a_superadmin_with_the_password_and_a_second_factor()
    {
        using var app = Enabled();
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var superAdmin = await SkillSwap.Api.IntegrationTests.Staff.StaffActor.CreateAsync(_factory, SkillSwap.Domain.Enums.UserRole.SuperAdmin);
        var admin = await SkillSwap.Api.IntegrationTests.Staff.StaffActor.CreateAsync(_factory, SkillSwap.Domain.Enums.UserRole.Admin);
        var moderator = await SkillSwap.Api.IntegrationTests.Staff.StaffActor.CreateAsync(_factory, SkillSwap.Domain.Enums.UserRole.Moderator);
        var (_, personal) = await SkillSwap.Api.IntegrationTests.Staff.StaffActor.PersonalAsync(_factory);

        Assert.Equal(HttpStatusCode.Forbidden, (await OpenPanelAsync(client, admin.Token, admin.Confirm())).StatusCode);   // админу нельзя
        Assert.Equal(HttpStatusCode.Forbidden, (await OpenPanelAsync(client, moderator.Token, moderator.Confirm())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await OpenPanelAsync(client, personal, null)).StatusCode);
        var none = await OpenPanelAsync(client, superAdmin.Token, null);
        var wrong = await OpenPanelAsync(client, superAdmin.Token, new { password = "Wrong-password-1", code = superAdmin.Recovery[superAdmin.Next++], isRecoveryCode = true });

        Assert.Equal("step_up_required", (await none.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("invalid_password", (await wrong.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == "HangfirePanelOpened" && a.ActorId == superAdmin.Id)));
    }

    [Fact]
    public async Task With_hangfire_switched_off_there_is_no_panel_and_no_ticket()
    {
        var superAdmin = await SkillSwap.Api.IntegrationTests.Staff.StaffActor.CreateAsync(_factory, SkillSwap.Domain.Enums.UserRole.SuperAdmin);
        using var app = _factory.WithSettings();
        var client = app.CreateClient();

        var response = await OpenPanelAsync(client, superAdmin.Token, superAdmin.Confirm());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("dashboard_unavailable", (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/hangfire")).StatusCode);
    }

    [Fact]
    public void Tickets_are_single_use_and_expire_and_sessions_expire()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var access = new HangfireDashboardAccess(Microsoft.Extensions.Options.Options.Create(new HangfireOptions { Enabled = true }), clock);
        var user = Guid.NewGuid();

        var (ticket, expires) = access.Issue(user);
        var (late, _) = access.Issue(user);
        var (unused, _) = access.Issue(user);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(1), expires);
        var session = access.Redeem(ticket);
        Assert.NotNull(session);
        Assert.Equal(user, session.Value.UserId);
        Assert.Null(access.Redeem(ticket));                          // второй раз нельзя
        Assert.Null(access.Redeem("garbage"));
        Assert.Equal(user, access.SessionUser(session.Value.Token));
        Assert.Null(access.SessionUser("garbage"));

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(access.Redeem(late));                            // билет истёк
        Assert.Equal(user, access.SessionUser(session.Value.Token));  // сессия ещё живёт
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Null(access.SessionUser(session.Value.Token));        // и она истекла
        Assert.Null(access.Redeem(unused));
    }

    [Fact]
    public async Task The_dashboard_is_not_there_when_hangfire_is_off_even_in_development()
    {
        using var app = _factory.WithSettings("Development");

        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync("/hangfire")).StatusCode);
    }
}
