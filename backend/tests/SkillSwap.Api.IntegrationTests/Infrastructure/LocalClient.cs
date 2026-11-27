using Microsoft.AspNetCore.Mvc.Testing;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>Клиент «с этого же компьютера» (127.0.0.1): маршруты /dev/* отвечают только loopback-адресам (см. DevToolsAccess), остальным 404.</summary>
public static class LocalClient
{
    public const string Ip = "127.0.0.1";

    public static HttpClient CreateLocalClient(this WebApplicationFactory<Program> app, WebApplicationFactoryClientOptions? options = null)
    {
        var client = options is null ? app.CreateClient() : app.CreateClient(options);
        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, Ip);
        return client;
    }
}
