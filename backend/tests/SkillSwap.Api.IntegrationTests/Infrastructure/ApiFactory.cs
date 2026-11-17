using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        // Настройки задаём переменными окружения: они видны приложению сразу при старте,
        // а Program.cs читает конфигурацию до ConfigureAppConfiguration.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "integration-tests-signing-key-0123456789-abcdef");

        // Применяем настоящие миграции проекта.
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");   // не Development: user-secrets разработчика не подмешиваются
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMailSender>();
            services.AddSingleton<IMailSender>(Mail);
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
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}
