using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;

namespace SkillSwap.Infrastructure.Storage;

/// <summary>
/// Проверка настроек хранилища при старте (fail fast): вне Development и Testing хранилище обязательно, без него загрузка файлов и аватаров
/// не работала бы. Ключи доступа, как и пароль SMTP, только из переменных окружения или user-secrets.
/// </summary>
public sealed class StorageStartupCheck : IHostedService
{
    private readonly StorageOptions _options;
    private readonly IHostEnvironment _environment;

    public StorageStartupCheck(IOptions<StorageOptions> options, IHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Validate(_options, _environment.IsDevelopment() || _environment.IsEnvironment("Testing"), _environment.EnvironmentName);
        return Task.CompletedTask;
    }

    public static void Validate(StorageOptions options, bool isDevelopmentLike, string environmentName)
    {
        if (!options.IsConfigured)
        {
            if (isDevelopmentLike)
            {
                return;   // загрузка файлов отвечает 503 с понятным кодом
            }

            throw new InvalidOperationException(
                $"В окружении {environmentName} не настроено хранилище файлов (Storage:Endpoint). Задайте Storage__Endpoint, Storage__AccessKey, " +
                "Storage__SecretKey и Storage__Bucket. Локально хранилище — Garage из docker-compose (см. docs/BACKEND-DEV.md).");
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Storage:Endpoint должен быть абсолютным адресом http(s), например http://localhost:3900.");
        }

        if (!string.IsNullOrWhiteSpace(options.PublicEndpoint)
            && (!Uri.TryCreate(options.PublicEndpoint, UriKind.Absolute, out var publicUri) || publicUri.Scheme is not ("http" or "https")))
        {
            throw new InvalidOperationException("Storage:PublicEndpoint должен быть абсолютным адресом http(s).");
        }

        if (string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
        {
            throw new InvalidOperationException("Не заданы Storage:AccessKey и Storage:SecretKey (только в переменных окружения или user-secrets).");
        }

        if (string.IsNullOrWhiteSpace(options.Bucket))
        {
            throw new InvalidOperationException("Не задан Storage:Bucket.");
        }

        if (!isDevelopmentLike && endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback)
        {
            throw new InvalidOperationException(
                $"В окружении {environmentName} Storage:Endpoint по http: ключи доступа и файлы шли бы по сети открытым текстом. Используйте https.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// При старте приводит бакет в порядок: проверяет, что он доступен, и разрешает браузеру фронтенда читать файлы по подписанным ссылкам
/// (CORS: только адрес фронтенда, только GET и HEAD). Сбой не останавливает приложение: хранилище может подняться позже,
/// ошибка пишется в журнал, а загрузки отвечают 503, пока оно недоступно.
/// </summary>
public sealed class StorageInitializer : IHostedService
{
    private readonly IFileStorage _storage;
    private readonly StorageOptions _options;
    private readonly FrontendOptions _frontend;
    private readonly ILogger<StorageInitializer> _logger;

    public StorageInitializer(IFileStorage storage, IOptions<StorageOptions> options, IOptions<FrontendOptions> frontend, ILogger<StorageInitializer> logger)
    {
        _storage = storage;
        _options = options.Value;
        _frontend = frontend.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_storage is not S3FileStorage s3)
        {
            return;   // хранилище не настроено (разработка): нечего готовить
        }

        try
        {
            await s3.Client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _options.Bucket, MaxKeys = 1 }, cancellationToken);

            await s3.Client.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
            {
                BucketName = _options.Bucket,
                Configuration = new CORSConfiguration
                {
                    Rules =
                    [
                        new CORSRule
                        {
                            AllowedOrigins = [_frontend.BaseUrl.TrimEnd('/')],
                            AllowedMethods = ["GET", "HEAD"],
                            AllowedHeaders = ["*"],
                            ExposeHeaders = ["Content-Length", "Content-Type", "Content-Disposition"],
                            MaxAgeSeconds = 3600,
                        },
                    ],
                },
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Хранилище файлов недоступно или бакет {Bucket} не настроен: загрузка файлов работать не будет, пока это не исправлено", _options.Bucket);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
