using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Настоящее S3-хранилище Garage в Docker (то же, что в docker-compose для разработки). После запуска контейнер настраивается так же,
/// как это делает скрипт <c>backend/scripts/init-storage.ps1</c>: раскладка кластера, ключ доступа, бакет.
/// </summary>
public sealed class GarageContainer
{
    public const string AccessKey = "GK0123456789abcdef01234567";
    public const string SecretKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    public const string Bucket = "skillswap-test";

    private const string Config = """
        metadata_dir = "/var/lib/garage/meta"
        data_dir = "/var/lib/garage/data"
        db_engine = "sqlite"
        replication_factor = 1
        rpc_bind_addr = "[::]:3901"
        rpc_public_addr = "127.0.0.1:3901"
        rpc_secret = "1799bccfd7411eddcf9ebd316bc1f5287ad12a68094e1c6ac968d73912bc1f3b"

        [s3_api]
        s3_region = "garage"
        api_bind_addr = "[::]:3900"
        """;

    private readonly IContainer _container = new ContainerBuilder("dxflrs/garage:v1.0.1")
        .WithResourceMapping(Encoding.UTF8.GetBytes(Config), "/etc/garage.toml")
        .WithPortBinding(3900, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("S3 API server listening"))
        .Build();

    /// <summary>Адрес S3 API, доступный тестам и приложению (оно в том же процессе).</summary>
    public string Endpoint => $"http://localhost:{_container.GetMappedPublicPort(3900)}";

    public async Task StartAsync()
    {
        await _container.StartAsync();

        var status = await RunAsync("/garage", "status");
        var nodeId = status.Split('\n')
            .Select(l => l.Trim())
            .SkipWhile(l => !l.StartsWith("ID", StringComparison.Ordinal))
            .Skip(1)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .FirstOrDefault(id => !string.IsNullOrEmpty(id))
            ?? throw new InvalidOperationException("Garage не показал идентификатор узла: " + status);

        await RunAsync("/garage", "layout", "assign", "-z", "dc1", "-c", "1G", nodeId);
        await RunAsync("/garage", "layout", "apply", "--version", "1");
        await RunAsync("/garage", "key", "import", "--yes", AccessKey, SecretKey, "-n", "skillswap-test");

        // Раскладка применяется не мгновенно: бакет создаём с повторами.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RunAsync("/garage", "bucket", "create", Bucket);
                break;
            }
            catch (InvalidOperationException) when (attempt < 20)
            {
                await Task.Delay(500);
            }
        }

        await RunAsync("/garage", "bucket", "allow", "--read", "--write", "--owner", Bucket, "--key", "skillswap-test");
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private async Task<string> RunAsync(params string[] command)
    {
        var result = await _container.ExecAsync(command);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{string.Join(' ', command)} завершилась с кодом {result.ExitCode}: {result.Stderr}");
        }

        return result.Stdout;
    }
}
