using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using SkillSwap.Application.Abstractions;
using SkillSwap.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Поднимает настоящее приложение поверх настоящего PostgreSQL в Docker (Testcontainers).
/// Один контейнер и одно приложение на все тесты коллекции; каждый тест работает со своим email.
/// Все настройки передаются конфигурацией приложения (а не переменными окружения процесса), поэтому
/// несколько независимых экземпляров с разными настройками (например, лимитами) могут работать параллельно.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();
    private readonly GarageContainer _garage = new();
    private string _connectionString = string.Empty;

    /// <summary>Строка подключения к БД тестового контейнера (для тестов, которым нужна своя пустая база на том же сервере).</summary>
    public string ConnectionString => _connectionString;

    // Тесты читают письма отсюда вместо консоли.
    public CapturingMailSender Mail { get; } = new();

    /// <summary>Серверный API LiveKit (удаление комнат) в тестах — заглушка.</summary>
    public FakeLiveKitRooms Rooms { get; } = new();

    /// <summary>
    /// Часы приложения, замороженные на моменте создания фабрики. Благодаря этому шаг TOTP в тесте не «ускользает»
    /// посреди теста, и можно детерминированно брать коды прошлого (−1), текущего (0) и следующего (+1) окна.
    /// </summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>Подставной сервер Cloudflare Turnstile: настоящих обращений в интернет в тестах нет.</summary>
    public FakeCloudflareHandler Cloudflare { get; } = new();

    /// <summary>Публичный тестовый секрет Cloudflare: допустим в окружении Testing.</summary>
    public const string TurnstileTestSecret = "1x0000000000000000000000000000000AA";

    /// <summary>Ключ и секрет «LiveKit» для тестов (секрет не короче 32 символов, как требует приложение).</summary>
    public const string LiveKitApiKey = "devkey";
    public const string LiveKitApiSecret = "livekit-integration-test-secret-0123456789-abcdef";
    public const string LiveKitUrl = "ws://livekit.test:7880";

    /// <summary>Адрес фронтенда для ссылок в письмах. Не localhost: приложение в окружении Production localhost не принимает.</summary>
    public const string FrontendUrl = "https://frontend.test";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _garage.StartAsync());

        _connectionString = _postgres.GetConnectionString();

        // Настоящие миграции проекта применяем ДО старта приложения: при старте сидер суперадмина
        // уже обращается к таблице Users.
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        _ = Services;   // стартуем приложение (и сидер суперадмина)
    }

    // Суперадмин, которого приложение создаёт при старте из настроек Superadmin.
    public const string SuperadminEmail = "root@skillswap.test";
    public const string SuperadminPassword = "Root-pass-123";

    /// <summary>
    /// Настройки приложения для этого экземпляра. По умолчанию ограничение частоты ВЫКЛЮЧЕНО: тысячи запросов
    /// тестов идут с нескольких адресов и упёрлись бы в лимиты. Наследники, которые проверяют лимиты, включают его.
    /// </summary>
    protected virtual void ConfigureSettings(IDictionary<string, string?> settings)
    {
        settings["RateLimiting:Enabled"] = "false";

        // Капча выключена: остальные тесты не передают токен. Секрет задан всё равно, как в реальном окружении.
        settings["Turnstile:Enabled"] = "false";
        settings["Turnstile:SecretKey"] = TurnstileTestSecret;

        settings["LiveKit:ApiKey"] = LiveKitApiKey;
        settings["LiveKit:ApiSecret"] = LiveKitApiSecret;
        settings["LiveKit:Url"] = LiveKitUrl;

        // Сроки урока — значения ТЗ в любом окружении, в том числе в Development: appsettings.Development.json сокращает их для ручной проверки,
        // а в тестах это недопустимо (сроки общие на процесс, и другой хост подменил бы их у остальных тестов).
        settings["Lessons:MinLeadMinutes"] = "60";
        settings["Lessons:FreeChangeMinutes"] = "120";
        settings["Lessons:JoinOpensBeforeMinutes"] = "10";
        settings["Lessons:FirstCheckMinutes"] = "15";
        settings["Lessons:JoinClosesAfterEndMinutes"] = "30";
        settings["Lessons:MinJointMinutes"] = "30";
        settings["Lessons:DurationMinutes"] = "45";
        settings["Lessons:ManualConfirmMinutes"] = "1440";
        settings["Cases:HighSlaMinutes"] = "1440";
        settings["Cases:NormalSlaMinutes"] = "4320";
        settings["Cases:ExplanationWindowMinutes"] = "2880";
        settings["Cases:ExplanationReminderBeforeMinutes"] = "360";
        settings["Cases:AppealWindowMinutes"] = "10080";

        // Фоновые задачи в тестах выполняются сразу, в процессе (без Hangfire и его таблиц): письма видны сразу после запроса.
        settings["Hangfire:Enabled"] = "false";

        // SMTP задан (как на боевом сервере): иначе приложения в окружении Production в тестах не запускались бы. Сами письма
        // в тестах перехватывает CapturingMailSender, на этот адрес ничего не уходит.
        settings["Smtp:Host"] = "smtp.skillswap.test";
        settings["Smtp:FromAddress"] = "no-reply@skillswap.test";
        settings["Frontend:BaseUrl"] = FrontendUrl;

        // Хранилище файлов: настоящий Garage в Docker.
        settings["Storage:Endpoint"] = _garage.Endpoint;
        settings["Storage:AccessKey"] = GarageContainer.AccessKey;
        settings["Storage:SecretKey"] = GarageContainer.SecretKey;
        settings["Storage:Bucket"] = GarageContainer.Bucket;

        // Ключи шифрования защищены сертификатом, как на боевом сервере (в тестах он создаётся на лету).
        settings["DataProtection:CertificateBase64"] = TestCertificates.Shared.Base64;
        settings["DataProtection:CertificatePassword"] = TestCertificates.Shared.Password;
    }

    /// <summary>
    /// Копия приложения на ТОЙ ЖЕ БД с дополнительными настройками и (при желании) другой средой, например Development.
    /// Новый контейнер не нужен: проверка «только в Development» и «LiveKit не настроен» обходится дёшево.
    /// </summary>
    public WebApplicationFactory<Program> WithSettings(string? environment = null, params (string Key, string? Value)[] settings) =>
        WithWebHostBuilder(builder =>
        {
            if (environment is not null)
            {
                builder.UseEnvironment(environment);
            }

            builder.ConfigureAppConfiguration((_, config) =>
            {
                // В Development хост подмешивает user-secrets разработчика (там лежит его настоящий dev-сертификат).
                // Тесты всегда работают со своим сертификатом, поэтому задаём его явно, а ваши значения идут поверх.
                var all = new Dictionary<string, string?>
                {
                    ["DataProtection:CertificatePath"] = "",
                    ["DataProtection:CertificateBase64"] = TestCertificates.Shared.Base64,
                    ["DataProtection:CertificatePassword"] = TestCertificates.Shared.Password,
                };
                foreach (var (key, value) in settings)
                {
                    all[key] = value;
                }
                config.AddInMemoryCollection(all);
            });
        });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");   // не Development: user-secrets разработчика не подмешиваются

        // Выполняется при создании хоста, то есть после StartAsync контейнера: строка подключения уже известна.
        // Остальные настройки приложение читает лениво, поэтому порядок источников конфигурации не важен.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _connectionString,
                ["Jwt:SigningKey"] = "integration-tests-signing-key-0123456789-abcdef",
                ["Superadmin:Email"] = SuperadminEmail,
                ["Superadmin:Password"] = SuperadminPassword,
            };
            ConfigureSettings(settings);
            config.AddInMemoryCollection(settings);
        });
        builder.ConfigureServices(services =>
        {
            // Хеширование паролей в тестах дешёвое (по умолчанию 100 000 итераций PBKDF2): проверяется логика, а не стойкость хеша. Только тестовая среда.
            services.Configure<Microsoft.AspNetCore.Identity.PasswordHasherOptions>(o => o.IterationCount = 1000);
            services.RemoveAll<IMailSender>();
            services.AddSingleton<IMailSender>(Mail);
            services.RemoveAll<ILiveKitRoomService>();
            services.AddSingleton<ILiveKitRoomService>(Rooms);

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            // Запросы к Cloudflare идут в заглушку (последняя регистрация первичного обработчика побеждает).
            services.AddHttpClient(SkillSwap.Infrastructure.Captcha.TurnstileCaptchaVerifier.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => Cloudflare);

            // У TestServer нет настоящего соединения, и RemoteIpAddress пуст. Задаём адрес клиента, как это сделал бы сокет.
            services.AddTransient<IStartupFilter, TestRemoteIpStartupFilter>();
        });
    }

    /// <summary>Выполняет SQL напрямую в БД теста (проверки «что реально лежит в таблице»).</summary>
    public async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _garage.DisposeAsync();
    }
}

/// <summary>
/// Подставляет IP клиента в тестовом сервере: по умолчанию 203.0.113.7 (адрес для документации),
/// либо значение заголовка <see cref="HeaderName"/>, если тест хочет «прийти» с другого адреса.
/// </summary>
internal sealed class TestRemoteIpStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Remote-Ip";
    public const string DefaultIp = "203.0.113.7";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            var ip = context.Request.Headers[HeaderName].FirstOrDefault() ?? DefaultIp;
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
            return nextMiddleware(context);
        });
        next(app);
    };
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
