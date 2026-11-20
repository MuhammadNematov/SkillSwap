using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;

namespace SkillSwap.Infrastructure.Jobs;

/// <summary>Настройки фоновых задач (секция "Hangfire").</summary>
public sealed class HangfireOptions
{
    public const string SectionName = "Hangfire";

    /// <summary>
    /// true — задачи выполняет сервер Hangfire (очередь в PostgreSQL). false — задачи выполняются сразу в процессе
    /// (<see cref="InlineBackgroundJobs"/>): так работают тесты. На боевом сервере всегда true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Сколько задач выполняется одновременно.</summary>
    public int WorkerCount { get; set; } = 4;

    /// <summary>Как часто сервер проверяет очередь, секунд: задержка выполнения задачи не больше этого значения.</summary>
    public int QueuePollIntervalSeconds { get; set; } = 2;
}

public static class HangfireSetup
{
    public const string Schema = "hangfire";
    public const string DashboardPath = "/hangfire";

    public static IServiceCollection AddBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HangfireOptions>()
            .Bind(configuration.GetSection(HangfireOptions.SectionName))
            .Validate(o => o.WorkerCount is >= 1 and <= 64, "Hangfire:WorkerCount должен быть от 1 до 64.")
            .Validate(o => o.QueuePollIntervalSeconds >= 1, "Hangfire:QueuePollIntervalSeconds должен быть положительным.")
            .ValidateOnStart();

        // Хранилище настраивается лениво, в момент первого обращения к Hangfire: строка подключения читается тогда же.
        // При Hangfire:Enabled=false к нему не обращается никто, и в БД ничего не создаётся.
        services.AddHangfire((sp, config) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(DependencyInjection.ConnectionStringName);
            var options = sp.GetRequiredService<IOptions<HangfireOptions>>().Value;

            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UsePostgreSqlStorage(
                    o => o.UseNpgsqlConnection(connectionString),
                    new PostgreSqlStorageOptions
                    {
                        SchemaName = Schema,
                        PrepareSchemaIfNecessary = true,
                        QueuePollInterval = TimeSpan.FromSeconds(options.QueuePollIntervalSeconds),
                    });
        });

        services.AddSingleton<HangfireBackgroundJobs>();
        services.AddSingleton<InlineBackgroundJobs>();
        services.AddSingleton<IBackgroundJobs>(sp => sp.GetRequiredService<IOptions<HangfireOptions>>().Value.Enabled
            ? sp.GetRequiredService<HangfireBackgroundJobs>()
            : sp.GetRequiredService<InlineBackgroundJobs>());

        services.AddHostedService<HangfireServerHostedService>();
        return services;
    }

    /// <summary>
    /// Панель Hangfire (<c>/hangfire</c>): только в Development и только когда Hangfire включён. На боевом сервере панели нет:
    /// она появится для суперадмина вместе с панелью суперадмина (этап 7).
    /// </summary>
    public static WebApplication UseDevelopmentHangfireDashboard(this WebApplication app)
    {
        if (app.Environment.IsDevelopment() && app.Services.GetRequiredService<IOptions<HangfireOptions>>().Value.Enabled)
        {
            app.UseHangfireDashboard(DashboardPath, new DashboardOptions
            {
                Authorization = [new DevelopmentOnlyDashboardFilter()],
                DashboardTitle = "SkillSwap: фоновые задачи",
            });
        }

        return app;
    }

    private sealed class DevelopmentOnlyDashboardFilter : IDashboardAuthorizationFilter
    {
        public bool Authorize(DashboardContext context) => true;   // панель подключается только в Development
    }
}

/// <summary>Запускает сервер Hangfire (обработчик очереди), если фоновые задачи включены.</summary>
public sealed class HangfireServerHostedService : IHostedService, IDisposable
{
    private readonly IOptions<HangfireOptions> _options;
    private readonly IServiceProvider _services;
    private BackgroundJobServer? _server;

    public HangfireServerHostedService(IOptions<HangfireOptions> options, IServiceProvider services)
    {
        _options = options;
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled)
        {
            return Task.CompletedTask;
        }

        _ = _services.GetRequiredService<IGlobalConfiguration>();   // настраивает хранилище и активатор задач через DI
        _server = new BackgroundJobServer(
            new BackgroundJobServerOptions
            {
                WorkerCount = _options.Value.WorkerCount,
                Queues = ["default"],
                ServerName = $"skillswap-{Environment.MachineName}-{Guid.NewGuid():N}",
            },
            _services.GetRequiredService<JobStorage>());
        SkillSwap.Application.Moderation.RecurringCatalog.Register(_services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.SendStop();
        return Task.CompletedTask;
    }

    public void Dispose() => _server?.Dispose();
}
