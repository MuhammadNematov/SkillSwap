using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

        services.AddSingleton<HangfireDashboardAccess>();
        services.AddSingleton<SkillSwap.Application.Staff.IDashboardTickets>(sp => sp.GetRequiredService<HangfireDashboardAccess>());
        services.AddSingleton<HangfireBackgroundJobs>();
        services.AddSingleton<InlineBackgroundJobs>();
        services.AddSingleton<IBackgroundJobs>(sp => sp.GetRequiredService<IOptions<HangfireOptions>>().Value.Enabled
            ? sp.GetRequiredService<HangfireBackgroundJobs>()
            : sp.GetRequiredService<InlineBackgroundJobs>());

        services.AddHostedService<HangfireServerHostedService>();
        return services;
    }

    /// <summary>
    /// Панель Hangfire (<c>/hangfire</c>) для суперадмина, во всех средах, где фоновые задачи включены. Войти можно только по одноразовому билету, который суперадмин получает после
    /// подтверждения паролем и 2FA (<c>POST /superadmin/hangfire/session</c>): сервер ставит короткую сессию в cookie, и каждый запрос панели проверяет по базе, что это действующий суперадмин
    /// (см. <see cref="HangfireDashboardFilter"/>). Без сессии панель отвечает 401.
    /// </summary>
    public static WebApplication UseSuperAdminHangfireDashboard(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<HangfireOptions>>().Value.Enabled)
        {
            app.UseHangfireDashboard(DashboardPath, new DashboardOptions
            {
                Authorization = [],   // стандартный фильтр «только с локального адреса» не нужен: вход решает асинхронный фильтр ниже
                AsyncAuthorization = [new HangfireDashboardFilter()],
                DashboardTitle = "SkillSwap: фоновые задачи",
                AppPath = null,   // ссылки «назад на сайт» не нужны: панель открывается отдельной вкладкой
            });
        }

        return app;
    }
}

/// <summary>
/// Одноразовые билеты и сессии панели Hangfire. Хранятся в памяти процесса (на нескольких экземплярах сервера сессия действует на том, где её открыли: при переходе на несколько экземпляров
/// хранилище нужно вынести в Redis). В памяти лежат только хэши билетов и токенов сессий, сами значения знает только владелец.
/// </summary>
public sealed class HangfireDashboardAccess : SkillSwap.Application.Staff.IDashboardTickets
{
    public const string CookieName = "skillswap_hangfire";

    private readonly IOptions<HangfireOptions> _options;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, (Guid UserId, DateTime ExpiresAt)> _tickets = [];
    private readonly Dictionary<string, (Guid UserId, DateTime ExpiresAt)> _sessions = [];

    public HangfireDashboardAccess(IOptions<HangfireOptions> options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    public bool Available => _options.Value.Enabled;

    public string Path => HangfireSetup.DashboardPath;

    public TimeSpan TicketLifetime { get; } = TimeSpan.FromMinutes(1);

    public TimeSpan SessionLifetime { get; } = TimeSpan.FromMinutes(30);

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    public (string Ticket, DateTime ExpiresAt) Issue(Guid userId)
    {
        var ticket = NewSecret();
        var expires = Now() + TicketLifetime;
        lock (_gate)
        {
            Purge();
            _tickets[Hash(ticket)] = (userId, expires);
        }

        return (ticket, expires);
    }

    /// <summary>Меняет билет на сессию: билет сгорает при первом же использовании (удачном или нет). null, если билета нет или он истёк.</summary>
    public (string Token, Guid UserId)? Redeem(string ticket)
    {
        lock (_gate)
        {
            Purge();
            if (!_tickets.Remove(Hash(ticket), out var found))
            {
                return null;
            }

            var token = NewSecret();
            _sessions[Hash(token)] = (found.UserId, Now() + SessionLifetime);
            return (token, found.UserId);
        }
    }

    /// <summary>Чья это сессия; null, если токена нет или он истёк.</summary>
    public Guid? SessionUser(string token)
    {
        lock (_gate)
        {
            Purge();
            return _sessions.TryGetValue(Hash(token), out var found) ? found.UserId : null;
        }
    }

    private void Purge()
    {
        var now = Now();
        foreach (var key in _tickets.Where(t => t.Value.ExpiresAt <= now).Select(t => t.Key).ToList())
        {
            _tickets.Remove(key);
        }

        foreach (var key in _sessions.Where(t => t.Value.ExpiresAt <= now).Select(t => t.Key).ToList())
        {
            _sessions.Remove(key);
        }
    }

    private static string NewSecret() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// Доступ к панели Hangfire: по cookie с сессией, которую выдал сервер в обмен на одноразовый билет (<c>?ticket=</c>). На каждый запрос проверяется по базе, что владелец сессии
/// всё ещё действующий суперадмин: снятого суперадмина панель перестаёт пускать сразу, не дожидаясь конца сессии.
/// </summary>
public sealed class HangfireDashboardFilter : IDashboardAsyncAuthorizationFilter
{
    public async Task<bool> AuthorizeAsync(DashboardContext context)
    {
        var http = context.GetHttpContext();
        var access = http.RequestServices.GetRequiredService<HangfireDashboardAccess>();
        Guid? userId = null;

        if (http.Request.Query.TryGetValue("ticket", out var ticket) && !string.IsNullOrEmpty(ticket.ToString()))
        {
            if (access.Redeem(ticket.ToString()!) is { } session)
            {
                userId = session.UserId;
                http.Response.Cookies.Append(HangfireDashboardAccess.CookieName, session.Token, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = http.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    Path = HangfireSetup.DashboardPath,
                    MaxAge = access.SessionLifetime,
                });
            }
        }
        else if (http.Request.Cookies.TryGetValue(HangfireDashboardAccess.CookieName, out var token) && !string.IsNullOrEmpty(token))
        {
            userId = access.SessionUser(token);
        }

        if (userId is null)
        {
            return false;
        }

        var user = await http.RequestServices.GetRequiredService<SkillSwap.Application.Profile.IProfileStore>().FindUserAsync(userId.Value, http.RequestAborted);
        return user is { Role: SkillSwap.Domain.Enums.UserRole.SuperAdmin, Status: SkillSwap.Domain.Enums.UserStatus.Active } && !user.IsRegular;
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
