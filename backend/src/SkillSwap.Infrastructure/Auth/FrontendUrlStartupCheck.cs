using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Options;

namespace SkillSwap.Infrastructure.Auth;

/// <summary>
/// Проверяет адрес фронтенда при старте (fail fast): на него ведут ссылки из писем (подтверждение email, сброс пароля).
/// Должен быть абсолютным адресом http(s). Вне Development и Testing адрес обязателен, должен быть https и не localhost:
/// иначе настоящим пользователям ушли бы письма со ссылкой на «localhost».
/// </summary>
public sealed class FrontendUrlStartupCheck : IHostedService
{
    private readonly FrontendOptions _options;
    private readonly IHostEnvironment _environment;

    public FrontendUrlStartupCheck(IOptions<FrontendOptions> options, IHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Validate(_options.BaseUrl, _environment.IsDevelopment() || _environment.IsEnvironment("Testing"), _environment.EnvironmentName);
        ValidatePaths(_options);
        return Task.CompletedTask;
    }

    public static void Validate(string? baseUrl, bool isDevelopmentLike, string environmentName)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "Frontend:BaseUrl должен быть абсолютным адресом http(s), например https://skillswap.example. " +
                "Он нужен для ссылок в письмах. Задайте его через настройки или переменную окружения Frontend__BaseUrl.");
        }

        if (isDevelopmentLike)
        {
            return;
        }

        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"В окружении {environmentName} Frontend:BaseUrl указывает на localhost: ссылки в письмах не откроются. " +
                "Задайте боевой адрес фронтенда (Frontend__BaseUrl).");
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"В окружении {environmentName} Frontend:BaseUrl должен быть https: в ссылках из писем идут одноразовые токены.");
        }
    }

    /// <summary>Маршруты для ссылок в письмах: путь начинается с «/», в пути обмена есть «{id}».</summary>
    public static void ValidatePaths(FrontendOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RequestsPath) || !options.RequestsPath.StartsWith('/'))
        {
            throw new InvalidOperationException("Frontend:RequestsPath должен начинаться с «/», например /requests.");
        }

        if (string.IsNullOrWhiteSpace(options.ExchangePath) || !options.ExchangePath.StartsWith('/') || !options.ExchangePath.Contains(FrontendOptions.IdPlaceholder))
        {
            throw new InvalidOperationException("Frontend:ExchangePath должен начинаться с «/» и содержать {id}, например /exchanges/{id}.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
