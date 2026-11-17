using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17").Build();

    // Тесты читают письма отсюда вместо консоли.
    public CapturingMailSender Mail { get; } = new();

    /// <summary>
    /// Часы приложения, замороженные на моменте создания фабрики. Благодаря этому шаг TOTP в тесте не «ускользает»
    /// посреди теста, и можно детерминированно брать коды прошлого (−1), текущего (0) и следующего (+1) окна.
    /// </summary>
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Настройки задаём переменными окружения: они видны приложению сразу при старте,
        // а Program.cs читает конфигурацию до ConfigureAppConfiguration.
        var connectionString = _postgres.GetConnectionString();
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", connectionString);
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "integration-tests-signing-key-0123456789-abcdef");
        Environment.SetEnvironmentVariable("Superadmin__Email", SuperadminEmail);
        Environment.SetEnvironmentVariable("Superadmin__Password", SuperadminPassword);

        // Настоящие миграции проекта применяем ДО старта приложения: при старте сидер суперадмина
        // уже обращается к таблице Users.
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        _ = Services;   // стартуем приложение (и сидер суперадмина)
    }

    // Суперадмин, которого приложение создаёт при старте из переменных окружения.
    public const string SuperadminEmail = "root@skillswap.test";
    public const string SuperadminPassword = "Root-pass-123";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");   // не Development: user-secrets разработчика не подмешиваются
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMailSender>();
            services.AddSingleton<IMailSender>(Mail);

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

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
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", null);
        Environment.SetEnvironmentVariable("Jwt__SigningKey", null);
        Environment.SetEnvironmentVariable("Superadmin__Email", null);
        Environment.SetEnvironmentVariable("Superadmin__Password", null);
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
