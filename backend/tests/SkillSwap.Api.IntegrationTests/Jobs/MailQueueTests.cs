using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkillSwap.Application.Abstractions;
using SkillSwap.Infrastructure.Email;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Jobs;

/// <summary>Письмо ставится в очередь зашифрованным, и ровно одной задачей; задача отправляет его через транспорт.</summary>
public class MailQueueTests
{
    private sealed class RecordingJobs : IBackgroundJobs
    {
        public List<System.Linq.Expressions.LambdaExpression> Jobs { get; } = [];

        public void Enqueue<T>(System.Linq.Expressions.Expression<Func<T, Task>> job) where T : notnull => Jobs.Add(job);

        public void Schedule<T>(System.Linq.Expressions.Expression<Func<T, Task>> job, DateTimeOffset runAt) where T : notnull => Jobs.Add(job);

        public void Recurring<T>(string id, System.Linq.Expressions.Expression<Func<T, Task>> job, string cron) where T : notnull => Jobs.Add(job);
    }

    private sealed class RecordingTransport : IMailTransport
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = [];

        public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
        {
            Sent.Add((to, subject, body));
            return Task.CompletedTask;
        }
    }

    private static IDataProtectionProvider NewProtection() => new EphemeralDataProtectionProvider();

    private const string Secret = "https://app.test/reset-password?userId=1&token=SUPER-SECRET-TOKEN";

    [Fact]
    public async Task Sending_a_letter_queues_exactly_one_job_and_does_not_touch_the_transport()
    {
        var jobs = new RecordingJobs();
        var sender = new BackgroundMailSender(jobs, NewProtection());

        await sender.SendAsync("alice@example.com", "Сброс пароля", Secret);

        Assert.Single(jobs.Jobs);
    }

    [Fact]
    public async Task The_body_with_the_token_is_not_readable_in_the_queued_job()
    {
        var jobs = new RecordingJobs();
        var sender = new BackgroundMailSender(jobs, NewProtection());

        await sender.SendAsync("alice@example.com", "Сброс пароля", Secret);

        var stored = jobs.Jobs.Single().ToString();   // то, что Hangfire сохранил бы в БД (аргументы)
        Assert.DoesNotContain("SUPER-SECRET-TOKEN", stored);
        Assert.DoesNotContain("reset-password", stored);
    }

    [Fact]
    public async Task The_job_decrypts_the_body_and_hands_the_original_letter_to_the_transport()
    {
        var protection = NewProtection();
        var jobs = new RecordingJobs();
        await new BackgroundMailSender(jobs, protection).SendAsync("alice@example.com", "Сброс пароля", Secret);
        var transport = new RecordingTransport();
        var job = new MailJobs(transport, protection);

        var call = (System.Linq.Expressions.MethodCallExpression)jobs.Jobs.Single().Body;
        var args = call.Arguments.Select(a => (string)System.Linq.Expressions.Expression.Lambda(a).Compile().DynamicInvoke()!).ToArray();
        await job.SendAsync(args[0], args[1], args[2]);

        Assert.Equal(("alice@example.com", "Сброс пароля", Secret), Assert.Single(transport.Sent));
    }

    [Fact]
    public async Task A_tampered_or_foreign_body_is_rejected_not_sent()
    {
        var transport = new RecordingTransport();
        var job = new MailJobs(transport, NewProtection());

        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() =>
            job.SendAsync("alice@example.com", "тема", "plain-text-not-protected"));

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public void The_mail_job_retries_a_few_times_with_growing_pauses()
    {
        var method = typeof(MailJobs).GetMethod(nameof(MailJobs.SendAsync))!;
        var retry = (Hangfire.AutomaticRetryAttribute)Attribute.GetCustomAttribute(method, typeof(Hangfire.AutomaticRetryAttribute))!;

        Assert.Equal(5, retry.Attempts);
        Assert.Equal(new[] { 10, 30, 120, 600, 3600 }, retry.DelaysInSeconds);
    }
}

/// <summary>Встроенный исполнитель задач (Hangfire:Enabled=false), которым пользуются тесты.</summary>
public class InlineBackgroundJobsTests
{
    public sealed class Counter
    {
        public int Runs;
        public List<string> Log { get; } = [];
    }

    public sealed class Probe(Counter counter)
    {
        public Task RunAsync(string text)
        {
            counter.Runs++;
            counter.Log.Add(text);
            return Task.CompletedTask;
        }

        public Task FailAsync() => throw new InvalidOperationException("boom");
    }

    private static (InlineBackgroundJobs Jobs, Counter Counter) Create()
    {
        var counter = new Counter();
        var services = new ServiceCollection().AddSingleton(counter).AddScoped<Probe>().BuildServiceProvider();
        return (new InlineBackgroundJobs(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<InlineBackgroundJobs>.Instance), counter);
    }

    [Fact]
    public void An_enqueued_job_runs_immediately_with_its_arguments()
    {
        var (jobs, counter) = Create();

        jobs.Enqueue<Probe>(p => p.RunAsync("привет"));

        Assert.Equal(["привет"], counter.Log);
    }

    [Fact]
    public void A_failing_job_does_not_break_the_caller_but_is_recorded()
    {
        var (jobs, _) = Create();

        jobs.Enqueue<Probe>(p => p.FailAsync());   // не бросает

        Assert.IsType<InvalidOperationException>(Assert.Single(jobs.Failures));
    }

    [Fact]
    public async Task A_scheduled_job_waits_until_its_time_and_runs_once_in_time_order()
    {
        var (jobs, counter) = Create();
        var t0 = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        jobs.Schedule<Probe>(p => p.RunAsync("later"), t0.AddHours(2));
        jobs.Schedule<Probe>(p => p.RunAsync("soon"), t0.AddHours(1));

        Assert.Equal(0, counter.Runs);                                // само не запускается
        Assert.Equal(0, await jobs.RunDueAsync(t0.AddMinutes(30)));   // время не пришло
        Assert.Equal(1, await jobs.RunDueAsync(t0.AddHours(1)));
        Assert.Equal(["soon"], counter.Log);
        Assert.Equal(1, await jobs.RunDueAsync(t0.AddHours(5)));
        Assert.Equal(["soon", "later"], counter.Log);
        Assert.Equal(0, await jobs.RunDueAsync(t0.AddHours(9)));      // второй раз не запускается
        Assert.Empty(jobs.Scheduled);
    }

    [Fact]
    public void Every_run_gets_its_own_scope()
    {
        var created = new List<object>();
        var services = new ServiceCollection().AddSingleton(created)
            .AddScoped<ScopedMarker>(sp => { var m = new ScopedMarker(); sp.GetRequiredService<List<object>>().Add(m); return m; })
            .AddScoped<ScopedProbe>().BuildServiceProvider();
        var jobs = new InlineBackgroundJobs(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<InlineBackgroundJobs>.Instance);

        jobs.Enqueue<ScopedProbe>(p => p.TouchAsync());
        jobs.Enqueue<ScopedProbe>(p => p.TouchAsync());

        Assert.Equal(2, created.Distinct().Count());
    }

    public sealed class ScopedMarker;

    public sealed class ScopedProbe(ScopedMarker marker)
    {
        public Task TouchAsync() => Task.FromResult(marker);
    }
}
