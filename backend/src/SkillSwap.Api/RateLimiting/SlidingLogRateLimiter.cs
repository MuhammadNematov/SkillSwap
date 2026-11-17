using System.Threading.RateLimiting;

namespace SkillSwap.Api.RateLimiting;

/// <summary>
/// Лимитер «скользящий журнал»: помнит моменты последних разрешённых запросов и пускает новый, если за
/// последние <c>window</c> их меньше <c>permitLimit</c>. В отличие от встроенного SlidingWindowRateLimiter,
/// при отказе знает точное время, когда освободится ближайшая попытка, и отдаёт его в метаданных
/// <see cref="MetadataName.RetryAfter"/> (у встроенного отказавшее разрешение этих метаданных не содержит,
/// а без них заголовок Retry-After пришлось бы выдумывать).
/// Остаётся обычным <see cref="RateLimiter"/>: подключается к <see cref="PartitionedRateLimiter"/> и middleware ASP.NET.
/// Время берётся из <see cref="TimeProvider"/>, поэтому в тестах окно «проходит» без реального ожидания.
/// Очереди нет: запрос либо пускается сразу, либо отклоняется.
/// </summary>
public sealed class SlidingLogRateLimiter : RateLimiter
{
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;
    private readonly Queue<long> _permits = new();   // метки времени разрешённых запросов по возрастанию
    private readonly object _gate = new();

    private long _lastActivity;
    private long _successful;
    private long _failed;
    private bool _disposed;

    public SlidingLogRateLimiter(int permitLimit, TimeSpan window, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);

        _permitLimit = permitLimit;
        _window = window;
        _clock = clock;
        _lastActivity = clock.GetTimestamp();
    }

    /// <summary>Время с последнего обращения, если сейчас нет ни одного учтённого запроса; иначе null (лимитер занят).</summary>
    public override TimeSpan? IdleDuration
    {
        get
        {
            lock (_gate)
            {
                Prune(_clock.GetTimestamp());
                return _permits.Count == 0 ? _clock.GetElapsedTime(_lastActivity) : null;
            }
        }
    }

    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_gate)
        {
            Prune(_clock.GetTimestamp());
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = _permitLimit - _permits.Count,
                CurrentQueuedCount = 0,
                TotalSuccessfulLeases = _successful,
                TotalFailedLeases = _failed,
            };
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _permitLimit);

            var now = _clock.GetTimestamp();
            _lastActivity = now;
            Prune(now);

            if (_permits.Count + permitCount <= _permitLimit)
            {
                for (var i = 0; i < permitCount; i++)
                {
                    _permits.Enqueue(now);
                }

                _successful++;
                return Lease.Success;
            }

            // Чтобы пустили, должно освободиться ровно столько мест, сколько не хватает. Места освобождаются
            // в порядке очереди: нужное — это старейшая метка с номером «не хватает» (из тех, что ещё в окне).
            var missing = _permits.Count + permitCount - _permitLimit;
            var blocking = _permits.ElementAt(Math.Max(1, missing) - 1);
            var retryAfter = _window - _clock.GetElapsedTime(blocking, now);

            _failed++;
            return new Lease(retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromMilliseconds(1));
        }
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
        new(AttemptAcquireCore(permitCount));

    protected override void Dispose(bool disposing)
    {
        lock (_gate)
        {
            _disposed = true;
            _permits.Clear();
        }
    }

    /// <summary>Убирает из журнала запросы, вышедшие за пределы окна. Вызывать под блокировкой.</summary>
    private void Prune(long now)
    {
        while (_permits.Count > 0 && _clock.GetElapsedTime(_permits.Peek(), now) >= _window)
        {
            _permits.Dequeue();
        }
    }

    private sealed class Lease : RateLimitLease
    {
        public static readonly Lease Success = new(null);

        private readonly TimeSpan? _retryAfter;

        public Lease(TimeSpan? retryAfter)
        {
            _retryAfter = retryAfter;
        }

        public override bool IsAcquired => _retryAfter is null;

        public override IEnumerable<string> MetadataNames =>
            _retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_retryAfter is { } value && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
