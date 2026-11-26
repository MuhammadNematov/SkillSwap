using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Options;
using SkillSwap.Application.Telegram;

namespace SkillSwap.Infrastructure.Telegram;

/// <summary>
/// Telegram Bot API по HTTP. Токен входит в путь запроса, поэтому: у клиента выключены журналы HttpClient (<c>RemoveAllLoggers</c>), а исключения и сообщения
/// об ошибках не содержат адреса запроса — только тип сбоя и описание, присланное Telegram.
/// </summary>
public sealed class TelegramBotClient : ITelegramClient
{
    public const string HttpClientName = "telegram";

    /// <summary>Клиент long polling: запрос ждёт обновления десятки секунд, поэтому таймаут больше.</summary>
    public const string PollingHttpClientName = "telegram-polling";

    private const string BaseAddress = "https://api.telegram.org";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly IHttpClientFactory _http;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramBotClient> _logger;

    public TelegramBotClient(IHttpClientFactory http, IOptions<TelegramOptions> options, ILogger<TelegramBotClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TelegramSendResult> SendMessageAsync(long chatId, string text, TelegramButton? button, CancellationToken ct)
    {
        var markup = button is null ? null : new { inline_keyboard = new[] { new[] { new { text = button.Text, url = button.Url } } } };
        var result = await SendAsync(new { chat_id = chatId, text, reply_markup = markup, disable_web_page_preview = true }, ct);
        if (result.Status == TelegramSendStatus.Rejected && button is not null && IsBadButton(result.Description))
        {
            // Telegram не принимает в кнопке адреса вроде localhost (разработка): ссылка уходит текстом
            return await SendAsync(new { chat_id = chatId, text = $"{text}\n{button.Url}", reply_markup = (object?)null, disable_web_page_preview = true }, ct);
        }

        return result;
    }

    private static bool IsBadButton(string? description) =>
        description is not null && (description.Contains("button", StringComparison.OrdinalIgnoreCase) || description.Contains("url", StringComparison.OrdinalIgnoreCase));

    private async Task<TelegramSendResult> SendAsync(object body, CancellationToken ct)
    {
        try
        {
            using var response = await PostAsync(HttpClientName, "sendMessage", body, ct);
            var reply = await ReadAsync<JsonElement>(response, ct);
            if (response.IsSuccessStatusCode && reply?.Ok == true)
            {
                return TelegramSendResult.Ok;
            }

            var description = reply?.Description ?? $"HTTP {(int)response.StatusCode}";
            return response.StatusCode switch
            {
                HttpStatusCode.Forbidden => new TelegramSendResult(TelegramSendStatus.Blocked, Description: description),
                HttpStatusCode.TooManyRequests => new TelegramSendResult(TelegramSendStatus.RateLimited, RetryAfterOf(response, reply), description),
                >= HttpStatusCode.InternalServerError => new TelegramSendResult(TelegramSendStatus.Transient, Description: description),
                HttpStatusCode.Unauthorized or HttpStatusCode.NotFound => Rejected(description, "Telegram не принял токен бота: проверьте Telegram:BotToken."),
                _ => new TelegramSendResult(TelegramSendStatus.Rejected, Description: description),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new TelegramSendResult(TelegramSendStatus.Transient, Description: Describe(ex));
        }
    }

    private TelegramSendResult Rejected(string description, string hint)
    {
        _logger.LogError("{Hint}", hint);
        return new TelegramSendResult(TelegramSendStatus.Rejected, Description: description);
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        var updates = await CallAsync<List<RawUpdate>>(PollingHttpClientName, "getUpdates", new { offset, timeout = timeoutSeconds, allowed_updates = new[] { "message" } }, ct);
        return (updates ?? []).Select(u => u.ToUpdate()).ToList();
    }

    public Task DeleteWebhookAsync(CancellationToken ct) => CallAsync<bool>(HttpClientName, "deleteWebhook", new { drop_pending_updates = false }, ct);

    public Task SetWebhookAsync(string url, string secretToken, CancellationToken ct) =>
        CallAsync<bool>(HttpClientName, "setWebhook", new { url, secret_token = secretToken, allowed_updates = new[] { "message" } }, ct);

    private async Task<T?> CallAsync<T>(string client, string method, object body, CancellationToken ct)
    {
        try
        {
            using var response = await PostAsync(client, method, body, ct);
            var reply = await ReadAsync<T>(response, ct);
            if (response.IsSuccessStatusCode && reply is { Ok: true })
            {
                return reply.Result;
            }

            throw new TelegramUnavailableException($"Telegram {method}: HTTP {(int)response.StatusCode} {reply?.Description}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new TelegramUnavailableException($"Telegram {method}: {Describe(ex)}");   // без исходного исключения: в нём мог бы оказаться адрес с токеном
        }
    }

    private Task<HttpResponseMessage> PostAsync(string client, string method, object body, CancellationToken ct) =>
        _http.CreateClient(client).PostAsJsonAsync($"{BaseAddress}/bot{_options.BotToken}/{method}", body, Json, ct);

    private static async Task<Reply<T>?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<Reply<T>>(Json, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan RetryAfterOf<T>(HttpResponseMessage response, Reply<T>? reply) =>
        reply?.Parameters?.RetryAfter is { } seconds ? TimeSpan.FromSeconds(seconds) : response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);

    private static string Describe(Exception ex) => ex is HttpRequestException http ? $"сбой сети ({http.HttpRequestError})" : "истекло время ожидания";

    private sealed record Reply<T>(bool Ok, T? Result, string? Description, ReplyParameters? Parameters);

    private sealed record ReplyParameters([property: JsonPropertyName("retry_after")] int? RetryAfter);

    internal sealed record RawUpdate([property: JsonPropertyName("update_id")] long UpdateId, RawMessage? Message)
    {
        public TelegramUpdate ToUpdate() => new(UpdateId, Message is { Chat: { } chat }
            ? new TelegramIncomingMessage(chat.Id, chat.Type == "private", Message.Text, Message.From?.LanguageCode)
            : null);
    }

    internal sealed record RawMessage(RawChat? Chat, RawUser? From, string? Text);

    internal sealed record RawChat(long Id, string? Type);

    internal sealed record RawUser([property: JsonPropertyName("language_code")] string? LanguageCode);

    /// <summary>Разбор тела webhook (то же, что элемент getUpdates).</summary>
    public static TelegramUpdate? ParseUpdate(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<RawUpdate>(body, Json) is { UpdateId: > 0 } raw ? raw.ToUpdate() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
