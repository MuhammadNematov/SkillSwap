using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Миграции при запуске: в Development приложение само догоняет схему базы, в остальных средах (Production, Testing) не трогает её
/// (на боевой базе миграции применяет отдельная команда при деплое). Проверяется на пустых базах, созданных на сервере тестового контейнера.
/// </summary>
[Collection(ApiCollection.Name)]
public class DevelopmentMigrationTests
{
    private readonly ApiFactory _factory;

    public DevelopmentMigrationTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Новая пустая база на том же сервере; возвращает строку подключения к ней.</summary>
    private async Task<string> NewEmptyDatabaseAsync()
    {
        var name = "migr_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(_factory.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_factory.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    private static AppDbContext Open(string connectionString) => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);

    private static async Task<bool> HasMigrationHistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL", connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_development_host_applies_all_migrations_to_an_empty_database()
    {
        var connectionString = await NewEmptyDatabaseAsync();

        using (var app = _factory.WithSettings("Development", ("ConnectionStrings:Default", connectionString)))
        {
            app.CreateClient();
        }

        await using var db = Open(connectionString);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(db.Database.GetMigrations().Count(), (await db.Database.GetAppliedMigrationsAsync()).Count());
        Assert.True(await db.Users.CountAsync() >= 0);   // таблица Users существует: запрос не падает
    }

    [Fact]
    public async Task A_development_host_applies_only_the_missing_migrations_to_a_database_that_is_behind()
    {
        var connectionString = await NewEmptyDatabaseAsync();
        string last;
        await using (var db = Open(connectionString))
        {
            var all = db.Database.GetMigrations().ToList();
            last = all[^1];
            await db.GetService<IMigrator>().MigrateAsync(all[^2]);   // база отстаёт на одну миграцию
            Assert.Contains(last, await db.Database.GetPendingMigrationsAsync());
        }

        using (var app = _factory.WithSettings("Development", ("ConnectionStrings:Default", connectionString)))
        {
            app.CreateClient();
        }

        await using var after = Open(connectionString);
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
        Assert.Contains(last, await after.Database.GetAppliedMigrationsAsync());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public async Task Other_environments_never_change_the_schema_themselves(string environment)
    {
        var connectionString = await NewEmptyDatabaseAsync();

        using (var app = _factory.WithSettings(environment, ("ConnectionStrings:Default", connectionString)))
        {
            // Без схемы такому хосту, возможно, и не запуститься (сидеры, ключи шифрования): здесь важно только, что миграции он не применил.
            _ = Record.Exception(() => app.CreateClient());
        }

        Assert.False(await HasMigrationHistoryAsync(connectionString));
    }
}
