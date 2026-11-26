using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Telegram;
using SkillSwap.Infrastructure.Telegram;

namespace SkillSwap.Api.IntegrationTests.Telegram;

/// <summary>Настоящий клиент Bot API с подставным HTTP: разбор ответов Telegram и отсутствие токена в журналах и сообщениях об ошибках.</summary>
[Collection(ApiCollection.Name)]
public class TelegramClientTests
{
    private const string Token = "123456:TEST-token-never-real";

    private readonly ApiFactory _factory;

    public TelegramClientTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.RequestUri!.ToString(), body));
            return answer(request, body);
        }
    }

    private sealed class Logs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception) + " " + exception);

        public void Dispose()
        {
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private (ITelegramClient Client, Handler Handler, Logs Logs, IDisposable Host) Create(Func<HttpRequestMessage, string, HttpResponseMessage> answer)
    {
        var handler = new Handler(answer);
        var logs = new Logs();
        var app = _factory.WithSettings(null, [.. ApiFactory.TelegramOn, ("Logging:LogLevel:Default", "Trace")]).WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITelegramClient>();
            services.AddSingleton<ITelegramClient, TelegramBotClient>();
            services.AddHttpClient(TelegramBotClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddHttpClient(TelegramBotClient.PollingHttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddLogging(l => l.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        }));
        return (app.Services.GetRequiredService<ITelegramClient>(), handler, logs, app);
    }

    [Fact]
    public async Task A_successful_send_posts_the_text_and_a_link_button()
    {
        var (client, handler, _, host) = Create((_, _) => Json(HttpStatusCode.OK, """{"ok":true,"result":{}}"""));
        using var _h = host;

        var result = await client.SendMessageAsync(42, "Привет", new TelegramButton("Открыть", "https://frontend.test/requests"), default);

        Assert.Equal(TelegramSendStatus.Sent, result.Status);
        var call = Assert.Single(handler.Calls, c => c.Url.EndsWith("/sendMessage"));
        Assert.Equal($"https://api.telegram.org/bot{Token}/sendMessage", call.Url);
        Assert.Contains("\"chat_id\":42", call.Body);
        Assert.Contains("inline_keyboard", call.Body);
        Assert.Contains("https://frontend.test/requests", call.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"ok":false,"description":"Forbidden: bot was blocked by the user"}""", TelegramSendStatus.Blocked, null)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"ok":false,"description":"Too Many Requests","parameters":{"retry_after":17}}""", TelegramSendStatus.RateLimited, 17)]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>", TelegramSendStatus.Transient, null)]
    [InlineData(HttpStatusCode.BadRequest, """{"ok":false,"description":"Bad Request: chat not found"}""", TelegramSendStatus.Rejected, null)]
    [InlineData(HttpStatusCode.Unauthorized, """{"ok":false,"description":"Unauthorized"}""", TelegramSendStatus.Rejected, null)]
    public async Task Telegram_answers_are_understood_and_the_token_never_reaches_the_logs(HttpStatusCode code, string body, TelegramSendStatus expected, int? retryAfter)
    {
        var (client, _, logs, host) = Create((_, _) => Json(code, body));
        using var _h = host;

        var result = await client.SendMessageAsync(1, "x", null, default);

        Assert.Equal(expected, result.Status);
        Assert.Equal(retryAfter, result.RetryAfter is { } r ? (int)r.TotalSeconds : null);
        Assert.DoesNotContain(logs.Lines, line => line.Contains(Token) || line.Contains("TEST-token"));
        Assert.DoesNotContain("TEST-token", result.Description ?? string.Empty);
    }

    [Fact]
    public async Task A_network_failure_is_transient_and_leaks_no_token_anywhere()
    {
        var (client, _, logs, host) = Create((request, _) => throw new HttpRequestException($"cannot reach {request.RequestUri}"));
        using var _h = host;

        var result = await client.SendMessageAsync(1, "x", null, default);
        var polling = await Assert.ThrowsAsync<TelegramUnavailableException>(() => client.GetUpdatesAsync(0, 1, default));
        var webhook = await Assert.ThrowsAsync<TelegramUnavailableException>(() => client.SetWebhookAsync("https://api.test/x", "s", default));

        Assert.Equal(TelegramSendStatus.Transient, result.Status);
        foreach (var text in new[] { result.Description ?? string.Empty, polling.ToString(), webhook.ToString() }.Concat(logs.Lines))
        {
            Assert.DoesNotContain("TEST-token", text);
        }
    }

    [Fact]
    public async Task A_link_telegram_refuses_in_a_button_is_sent_as_text()
    {
        var (client, handler, _, host) = Create((_, body) => body.Contains("inline_keyboard")
            ? Json(HttpStatusCode.BadRequest, """{"ok":false,"description":"Bad Request: inline keyboard button URL 'http://localhost:5173/requests' is invalid: Wrong HTTP URL"}""")
            : Json(HttpStatusCode.OK, """{"ok":true,"result":{}}"""));
        using var _h = host;

        var result = await client.SendMessageAsync(7, "Новая заявка", new TelegramButton("Открыть", "http://localhost:5173/requests"), default);

        Assert.Equal(TelegramSendStatus.Sent, result.Status);
        var last = handler.Calls[^1].Body;
        Assert.DoesNotContain("inline_keyboard", last);
        Assert.Contains("http://localhost:5173/requests", last);
    }

    [Fact]
    public async Task Updates_from_long_polling_keep_private_chats_apart_from_groups()
    {
        var (client, _, _, host) = Create((_, _) => Json(HttpStatusCode.OK,
            """{"ok":true,"result":[{"update_id":5,"message":{"chat":{"id":10,"type":"private"},"from":{"language_code":"ru"},"text":"/start abc"}},{"update_id":6,"message":{"chat":{"id":-100,"type":"group"},"text":"/stop"}},{"update_id":7}]}"""));
        using var _h = host;

        var updates = await client.GetUpdatesAsync(0, 0, default);

        Assert.Equal([5, 6, 7], updates.Select(u => u.UpdateId));
        Assert.True(updates[0].Message!.IsPrivate);
        Assert.Equal("/start abc", updates[0].Message!.Text);
        Assert.False(updates[1].Message!.IsPrivate);
        Assert.Null(updates[2].Message);
    }
}
