using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SkillSwap.Infrastructure.Persistence;

/// <summary>
/// В Development применяет недостающие миграции при запуске: локальная база после <c>git pull</c> не отстаёт от кода (раньше приходилось помнить про
/// <c>dotnet ef database update</c>, а забытая миграция давала 500 на первом же запросе к новой колонке).
/// В остальных средах (в том числе Production и Testing) не делает ничего: на боевой базе миграции применяются отдельной командой при деплое, чтобы приложение
/// не меняло схему само и не получало на это права. Регистрируется раньше сидеров и фоновых задач, поэтому они стартуют уже на актуальной схеме.
/// </summary>
public sealed class DevelopmentMigrationRunner : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<DevelopmentMigrationRunner> _logger;

    public DevelopmentMigrationRunner(IServiceScopeFactory scopes, IHostEnvironment environment, ILogger<DevelopmentMigrationRunner> logger)
    {
        _scopes = scopes;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            _logger.LogInformation("Development: схема базы актуальна, миграции не нужны.");
            return;
        }

        _logger.LogWarning("Development: применяются миграции ({Count}): {Migrations}.", pending.Count, string.Join(", ", pending));
        await db.Database.MigrateAsync(cancellationToken);
        _logger.LogInformation("Development: миграции применены.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
