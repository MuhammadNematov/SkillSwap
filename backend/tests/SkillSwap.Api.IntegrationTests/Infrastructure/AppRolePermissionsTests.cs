using Microsoft.EntityFrameworkCore;
using Npgsql;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Права роли приложения на боевой базе (этап 7, шаг 9): скрипт <c>scripts/create-app-role.sql</c> даёт роли <c>skillswap_app</c> обычную работу с данными, а на журнал аудита
/// только SELECT и INSERT. Проверяется на настоящем PostgreSQL: сначала скрипт выполняет владелец, потом проверки идут уже под самой ролью.
/// </summary>
[Collection(ApiCollection.Name)]
public class AppRolePermissionsTests
{
    private const string Role = "skillswap_app";
    private const string RolePassword = "app-role-test-password-1";
    private const string InsufficientPrivilege = "42501";

    private readonly ApiFactory _factory;

    public AppRolePermissionsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private static string ScriptText() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts", "create-app-role.sql"));

    /// <summary>Новая база с настоящей схемой (все миграции) на сервере тестового контейнера, владелец — суперпользователь контейнера.</summary>
    private async Task<(string Owner, string App)> PreparedDatabaseAsync(bool runScript = true)
    {
        var name = "role_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(_factory.ConnectionString))
        {
            await admin.OpenAsync();
            await ExecuteAsync(admin, $"CREATE DATABASE \"{name}\"");
        }

        var owner = new NpgsqlConnectionStringBuilder(_factory.ConnectionString) { Database = name, Pooling = false };
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(owner.ConnectionString).Options))
        {
            await db.Database.MigrateAsync();
        }

        await using (var connection = new NpgsqlConnection(owner.ConnectionString))
        {
            await connection.OpenAsync();
            // Роль создаётся заранее и отдельно (так делает владелец при деплое), пароль в скрипте не хранится. Роли общие на весь сервер, поэтому «если нет».
            await ExecuteAsync(connection, $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{Role}') THEN CREATE ROLE {Role} LOGIN PASSWORD '{RolePassword}'; END IF; END $$;");
            if (runScript)
            {
                await ExecuteAsync(connection, ScriptText());
            }
        }

        var app = new NpgsqlConnectionStringBuilder(owner.ConnectionString) { Username = Role, Password = RolePassword };
        return (owner.ConnectionString, app.ConnectionString);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> AsAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertDeniedAsync(string connectionString, string sql)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => AsAsync(connectionString, sql));
        Assert.Equal(InsufficientPrivilege, error.SqlState);
    }

    private const string InsertAudit = "INSERT INTO \"AuditLogs\" (\"Id\", \"Action\", \"TargetType\", \"DetailsJson\", \"CreatedAt\") VALUES (gen_random_uuid(), 'RoleCheck', 'System', '{}', now())";

    [Fact]
    public async Task The_application_role_may_add_and_read_the_audit_log_but_not_change_delete_or_truncate_it()
    {
        var (_, app) = await PreparedDatabaseAsync();

        Assert.Equal(1, await AsAsync(app, InsertAudit));
        await using (var connection = new NpgsqlConnection(app))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT count(*) FROM \"AuditLogs\"", connection);
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }

        await AssertDeniedAsync(app, "UPDATE \"AuditLogs\" SET \"Action\" = 'Changed'");
        await AssertDeniedAsync(app, "UPDATE \"AuditLogs\" SET \"Action\" = 'Changed' WHERE false");   // права проверяются до выборки строк
        await AssertDeniedAsync(app, "DELETE FROM \"AuditLogs\"");
        await AssertDeniedAsync(app, "TRUNCATE \"AuditLogs\"");
    }

    [Fact]
    public async Task The_application_role_works_with_all_other_tables_as_usual()
    {
        var (owner, app) = await PreparedDatabaseAsync();

        foreach (var table in new[] { "Users", "Notifications", "Cases", "Attachments", "StaffInvitations", "PersonalLinkRequests", "DataProtectionKeys" })
        {
            Assert.Equal(0, await AsAsync(app, $"UPDATE \"{table}\" SET \"Id\" = \"Id\" WHERE false"));
            Assert.Equal(0, await AsAsync(app, $"DELETE FROM \"{table}\" WHERE false"));
        }

        // Таблица, которую создаст будущая миграция под владельцем, тоже сразу доступна приложению.
        await AsAsync(owner, "CREATE TABLE \"FutureTable\" (\"Id\" serial PRIMARY KEY, \"Name\" text)");
        Assert.Equal(1, await AsAsync(app, "INSERT INTO \"FutureTable\" (\"Name\") VALUES ('a')"));   // и последовательность с ней
        Assert.Equal(1, await AsAsync(app, "UPDATE \"FutureTable\" SET \"Name\" = 'b'"));
        Assert.Equal(1, await AsAsync(app, "DELETE FROM \"FutureTable\""));
    }

    [Fact]
    public async Task The_application_role_only_reads_the_migration_history_and_cannot_create_tables_in_the_main_schema()
    {
        var (_, app) = await PreparedDatabaseAsync();

        await AsAsync(app, "SELECT 1 FROM \"__EFMigrationsHistory\" LIMIT 1");   // читать можно: отказ бросил бы исключение
        await AssertDeniedAsync(app, "DELETE FROM \"__EFMigrationsHistory\" WHERE false");
        await AssertDeniedAsync(app, "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('x', 'y')");
        await AssertDeniedAsync(app, "CREATE TABLE public.\"Rogue\" (\"Id\" int)");
        await AssertDeniedAsync(app, "ALTER TABLE \"AuditLogs\" DISABLE TRIGGER ALL");
    }

    [Fact]
    public async Task The_hangfire_schema_is_available_to_the_application_role()
    {
        var (_, app) = await PreparedDatabaseAsync();

        await AsAsync(app, "CREATE TABLE hangfire.\"ProbeJobs\" (\"Id\" int); INSERT INTO hangfire.\"ProbeJobs\" VALUES (1); DROP TABLE hangfire.\"ProbeJobs\"");   // Hangfire создаёт и меняет свои таблицы сам
    }

    [Fact]
    public async Task The_script_can_be_run_again_and_a_new_run_takes_back_a_privilege_given_by_mistake()
    {
        var (owner, app) = await PreparedDatabaseAsync();
        await AsAsync(owner, $"GRANT UPDATE, DELETE ON \"AuditLogs\" TO {Role}");   // кто-то выдал лишнее вручную
        Assert.Equal(0, await AsAsync(app, "UPDATE \"AuditLogs\" SET \"Action\" = \"Action\" WHERE false"));

        await AsAsync(owner, ScriptText());
        await AsAsync(owner, ScriptText());   // повторный запуск не падает

        await AssertDeniedAsync(app, "UPDATE \"AuditLogs\" SET \"Action\" = \"Action\" WHERE false");
        await AssertDeniedAsync(app, "DELETE FROM \"AuditLogs\" WHERE false");
        Assert.Equal(1, await AsAsync(app, InsertAudit));
    }

    [Fact]
    public async Task The_script_carries_the_role_instructions_and_no_password()
    {
        var script = ScriptText();
        Assert.Contains("CREATE ROLE skillswap_app", script);   // инструкция по созданию роли в самом скрипте
        Assert.DoesNotContain("PASSWORD '", script.Replace("PASSWORD ''<пароль>''", string.Empty).Replace("PASSWORD '<пароль>'", string.Empty));   // пароля в репозитории нет
        Assert.Contains("REVOKE UPDATE, DELETE, TRUNCATE", script);
    }
}
