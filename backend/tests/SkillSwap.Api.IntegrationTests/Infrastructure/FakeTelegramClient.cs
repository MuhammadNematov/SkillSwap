using System.Collections.Concurrent;
using SkillSwap.Application.Telegram;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>Bot API в тестах: настоящих обращений к Telegram нет. Отправленное запоминается; следующим отправкам можно задать ответы (403, 429, сбой).</summary>
public sealed class FakeTelegramClient : ITelegramClient
{
    public sealed record SentMessage(long ChatId, string Text, TelegramButton? Button);

    private readonly ConcurrentQueue<SentMessage> _sent = new();
    private readonly ConcurrentDictionary<long, ConcurrentQueue<TelegramSendResult>> _replies = new();
    private readonly ConcurrentQueue<TelegramUpdate> _updates = new();

    public IReadOnlyList<SentMessage> Sent => _sent.ToArray();

    public IReadOnlyList<SentMessage> To(long chatId) => _sent.Where(m => m.ChatId == chatId).ToList();

    public List<(string Url, string Secret)> Webhooks { get; } = [];

    public int DeletedWebhooks;

    public List<long> RequestedOffsets { get; } = [];

    /// <summary>Следующие отправки в этот чат получат эти ответы (по очереди), потом снова успех.</summary>
    public void Reply(long chatId, params TelegramSendResult[] results)
    {
        var queue = _replies.GetOrAdd(chatId, _ => new ConcurrentQueue<TelegramSendResult>());
        foreach (var r in results)
        {
            queue.Enqueue(r);
        }
    }

    public void PushUpdate(TelegramUpdate update) => _updates.Enqueue(update);

    public List<int> RequestedTimeouts { get; } = [];

    /// <summary>Столько следующих запросов обновлений «обрываются» (как долгий опрос на плохой сети).</summary>
    public int FailPolls;

    public Task<TelegramSendResult> SendMessageAsync(long chatId, string text, TelegramButton? button, CancellationToken ct)
    {
        var result = _replies.TryGetValue(chatId, out var queue) && queue.TryDequeue(out var next) ? next : TelegramSendResult.Ok;
        if (result.Status == TelegramSendStatus.Sent)
        {
            _sent.Enqueue(new SentMessage(chatId, text, button));
        }

        return Task.FromResult(result);
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        lock (RequestedOffsets)
        {
            RequestedOffsets.Add(offset);
            RequestedTimeouts.Add(timeoutSeconds);
        }

        if (Interlocked.Decrement(ref FailPolls) >= 0)
        {
            throw new TelegramUnavailableException("Telegram getUpdates: истекло время ожидания");
        }

        var batch = new List<TelegramUpdate>();
        while (_updates.TryDequeue(out var update))
        {
            batch.Add(update);
        }

        if (batch.Count == 0)
        {
            await Task.Delay(100, ct);
        }

        return batch;
    }

    public Task DeleteWebhookAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref DeletedWebhooks);
        return Task.CompletedTask;
    }

    public Task SetWebhookAsync(string url, string secretToken, CancellationToken ct)
    {
        lock (Webhooks)
        {
            Webhooks.Add((url, secretToken));
        }

        return Task.CompletedTask;
    }
}
