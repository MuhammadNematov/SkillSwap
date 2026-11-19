using System.Collections.Concurrent;
using SkillSwap.Application.Exceptions;

namespace SkillSwap.Application.Chat;

/// <summary>
/// Защита от залпа сообщений: не больше <see cref="Limit"/> отправок за <see cref="Window"/> на пользователя. Счётчики хранятся в памяти
/// процесса (Redis не используется, решение для этапа 4); при нескольких экземплярах лимит действует на каждый отдельно.
/// </summary>
public sealed class ChatThrottle
{
    public const int Limit = 20;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<Guid, Queue<DateTime>> _sent = new();

    public ChatThrottle(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Учитывает одну отправку или бросает 429 <c>chat_rate_limited</c>.</summary>
    public void Hit(Guid userId)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var queue = _sent.GetOrAdd(userId, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= Limit)
            {
                throw new TooManyRequestsException("chat_rate_limited", "Слишком много сообщений подряд. Подождите несколько секунд.");
            }

            queue.Enqueue(now);
        }
    }
}
