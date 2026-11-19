using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace SkillSwap.Api.IntegrationTests.Jobs;

/// <summary>
/// Настоящий SMTP-сервер Mailpit в Docker (тот же, что в docker-compose для разработки): письма уходят по настоящему протоколу SMTP,
/// а проверяются через его HTTP API.
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:v1.21")
        .WithEnvironment("MP_SMTP_AUTH_ACCEPT_ANY", "1")        // принимает любой логин и пароль, чтобы проверить вход с учётными данными
        .WithEnvironment("MP_SMTP_AUTH_ALLOW_INSECURE", "1")     // разрешает логин по соединению без TLS (только для теста)
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/livez")))
        .Build();

    private readonly HttpClient _http = new();

    public string Host => "localhost";
    public int SmtpPort => _container.GetMappedPublicPort(1025);
    private string Api => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8025)}/api/v1";

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _container.DisposeAsync();
    }

    public async Task ClearAsync() => (await _http.DeleteAsync($"{Api}/messages")).EnsureSuccessStatusCode();

    public sealed record Mail(string Id, string From, string FromName, string[] To, string Subject, string Text, string Raw);

    /// <summary>Все письма, которые сервер принял (ждёт появления нужного числа до таймаута).</summary>
    public async Task<List<Mail>> WaitForAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            var list = await _http.GetFromJsonAsync<JsonElement>($"{Api}/messages");
            var messages = list.GetProperty("messages").EnumerateArray().ToList();
            if (messages.Count >= count || DateTime.UtcNow > deadline)
            {
                var result = new List<Mail>();
                foreach (var m in messages)
                {
                    var id = m.GetProperty("ID").GetString()!;
                    var detail = await _http.GetFromJsonAsync<JsonElement>($"{Api}/message/{id}");
                    var raw = await _http.GetStringAsync($"{Api}/message/{id}/raw");
                    result.Add(new Mail(
                        id,
                        detail.GetProperty("From").GetProperty("Address").GetString()!,
                        detail.GetProperty("From").GetProperty("Name").GetString()!,
                        detail.GetProperty("To").EnumerateArray().Select(t => t.GetProperty("Address").GetString()!).ToArray(),
                        detail.GetProperty("Subject").GetString()!,
                        detail.GetProperty("Text").GetString()!,
                        raw));
                }

                return result;
            }

            await Task.Delay(200);
        }
    }
}
