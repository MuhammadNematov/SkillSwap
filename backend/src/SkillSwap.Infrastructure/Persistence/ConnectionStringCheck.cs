using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SkillSwap.Infrastructure.Persistence;

/// <summary>
/// Проверяет при старте приложения, что строка подключения к БД задана (fail fast).
/// Сама строка читается лениво, в момент создания DbContext, поэтому её можно задать любым источником
/// конфигурации, в том числе добавленным уже после регистрации сервисов (так делают интеграционные тесты).
/// </summary>
internal sealed class ConnectionStringCheck : IHostedService
{
    private readonly IConfiguration _configuration;

    public ConnectionStringCheck(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_configuration.GetConnectionString(DependencyInjection.ConnectionStringName)))
        {
            throw new InvalidOperationException(
                $"Не задана строка подключения ConnectionStrings:{DependencyInjection.ConnectionStringName}. " +
                "Задайте её через user-secrets или переменную окружения ConnectionStrings__Default.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
