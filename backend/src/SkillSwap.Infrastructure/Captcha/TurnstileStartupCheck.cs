using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Options;

namespace SkillSwap.Infrastructure.Captcha;

/// <summary>
/// Проверяет настройки капчи при старте приложения (fail fast):
/// секрет обязателен, а публичный тестовый секрет Cloudflare допустим только в Development и Testing.
/// Иначе приложение могло бы выйти в бой с ключом «всегда пускать», то есть без капчи.
/// </summary>
public sealed class TurnstileStartupCheck : IHostedService
{
    private readonly TurnstileOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<TurnstileStartupCheck> _logger;

    public TurnstileStartupCheck(IOptions<TurnstileOptions> options, IHostEnvironment environment, ILogger<TurnstileStartupCheck> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("Капча Turnstile выключена (Turnstile:Enabled=false). На боевом сервере она должна быть включена.");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            throw new InvalidOperationException(
                "Не задан секрет Cloudflare Turnstile (Turnstile:SecretKey). Задайте его через user-secrets или переменную " +
                "окружения Turnstile__SecretKey. Для разработки подойдёт тестовый секрет 1x0000000000000000000000000000000AA.");
        }

        var testEnvironment = _environment.IsDevelopment() || _environment.IsEnvironment("Testing");
        if (TurnstileOptions.IsDummySecret(_options.SecretKey) && !testEnvironment)
        {
            throw new InvalidOperationException(
                $"В окружении {_environment.EnvironmentName} задан ТЕСТОВЫЙ секрет Turnstile: он пропускает любого бота. " +
                "Задайте настоящий секрет из панели Cloudflare.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
