using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Options;
using SkillSwap.Application.Telegram;

namespace SkillSwap.Infrastructure.Telegram;

/// <summary>Development: long polling (<c>getUpdates</c>), вебхук не нужен. Запускается только если бот настроен.</summary>
public sealed class TelegramPollingService : BackgroundService
{
    /// <summary>Сколько Telegram держит запрос в ожидании обновлений. После обрыва окно сокращается: часть сетей (мобильный интернет, NAT) обрывает долгие простаивающие соединения.</summary>
    public const int LongPollSeconds = 25;

    public const int ShortPollSeconds = 5;

    private readonly IServiceScopeFactory _scopes;
    private readonly ITelegramClient _client;
    private readonly TelegramOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<TelegramPollingService> _logger;

    public TelegramPollingService(
        IServiceScopeFactory scopes, ITelegramClient client, IOptions<TelegramOptions> options, IHostEnvironment environment, ILogger<TelegramPollingService> logger)
    {
        _scopes = scopes;
        _client = client;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_environment.IsDevelopment() || !_options.IsConfigured || !_options.Polling)
        {
            return;
        }

        await Task.Yield();   // не задерживать запуск приложения
        long offset = 0;
        var webhookRemoved = false;
        var pollSeconds = LongPollSeconds;
        _logger.LogInformation("Telegram: long polling запущен для бота @{Bot}.", _options.BotName);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!webhookRemoved)
                {
                    await _client.DeleteWebhookAsync(stoppingToken);   // с установленным webhook getUpdates отвечает 409
                    webhookRemoved = true;
                }

                foreach (var update in await _client.GetUpdatesAsync(offset, pollSeconds, stoppingToken))
                {
                    offset = update.UpdateId + 1;   // обработанное обновление не запрашиваем снова, даже если обработка упала
                    await HandleAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (webhookRemoved && pollSeconds > ShortPollSeconds)
                {
                    pollSeconds = ShortPollSeconds;
                    _logger.LogWarning("Telegram: долгий опрос не удался ({Message}). Дальше окно ожидания {Seconds} с.", ex.Message, pollSeconds);
                    continue;
                }

                _logger.LogWarning("Telegram: опрос не удался ({Message}). Повтор через 5 секунд.", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task HandleAsync(TelegramUpdate update, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TelegramUpdateHandler>().HandleAsync(update, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Telegram: не удалось обработать обновление {UpdateId}.", update.UpdateId);
        }
    }
}

/// <summary>Настройки webhook вне Development: с настроенным ботом нужны секрет и https-адрес, иначе приложение не запускается.</summary>
public static class TelegramWebhookSettings
{
    public const string Path = "/api/v1/webhooks/telegram";

    private static readonly Regex Secret = new("^[A-Za-z0-9_-]{16,256}$", RegexOptions.Compiled);

    public static string Url(TelegramOptions options, FrontendOptions frontend) =>
        (string.IsNullOrWhiteSpace(options.WebhookBaseUrl) ? frontend.BaseUrl : options.WebhookBaseUrl).TrimEnd('/') + Path;

    public static string? Problem(TelegramOptions options, FrontendOptions frontend)
    {
        if (!Secret.IsMatch(options.WebhookSecret))
        {
            return "Telegram:WebhookSecret обязателен вне Development: 16–256 символов из латиницы, цифр, «_» и «-».";
        }

        return Uri.TryCreate(Url(options, frontend), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
            ? null
            : "Адрес webhook Telegram должен быть https: задайте Telegram:WebhookBaseUrl или https в Frontend:BaseUrl.";
    }
}

public sealed class TelegramStartupCheck : IHostedService
{
    private readonly IOptions<TelegramOptions> _options;
    private readonly IOptions<FrontendOptions> _frontend;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<TelegramStartupCheck> _logger;

    public TelegramStartupCheck(IOptions<TelegramOptions> options, IOptions<FrontendOptions> frontend, IHostEnvironment environment, ILogger<TelegramStartupCheck> logger)
    {
        _options = options;
        _frontend = frontend;
        _environment = environment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.IsConfigured)
        {
            _logger.LogInformation("Telegram: бот не настроен (Telegram:BotToken и Telegram:BotUsername), уведомления в Telegram выключены.");
            return Task.CompletedTask;
        }

        if (!_environment.IsDevelopment() && TelegramWebhookSettings.Problem(options, _frontend.Value) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Вне Development: ставит webhook при запуске (в фоне, с повторами, чтобы сбой Telegram не мешал запуску).</summary>
public sealed class TelegramWebhookRegistration : BackgroundService
{
    private readonly ITelegramClient _client;
    private readonly TelegramOptions _options;
    private readonly FrontendOptions _frontend;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<TelegramWebhookRegistration> _logger;

    public TelegramWebhookRegistration(
        ITelegramClient client, IOptions<TelegramOptions> options, IOptions<FrontendOptions> frontend, IHostEnvironment environment,
        ILogger<TelegramWebhookRegistration> logger)
    {
        _client = client;
        _options = options.Value;
        _frontend = frontend.Value;
        _environment = environment;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_environment.IsDevelopment() || !_options.IsConfigured)
        {
            return;
        }

        await Task.Yield();
        var url = TelegramWebhookSettings.Url(_options, _frontend);
        var delay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _client.SetWebhookAsync(url, _options.WebhookSecret, stoppingToken);
                _logger.LogInformation("Telegram: webhook установлен на {Url}.", url);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError("Telegram: не удалось установить webhook ({Message}). Повтор через {Delay}.", ex.Message, delay);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        }
    }
}
