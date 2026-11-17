using System.Threading.RateLimiting;
using Microsoft.Extensions.Time.Testing;
using SkillSwap.Api.RateLimiting;

namespace SkillSwap.Api.IntegrationTests.RateLimiting;

/// <summary>Лимитер «скользящий журнал» на детерминированных часах: окно проходит без реального ожидания.</summary>
public class SlidingLogRateLimiterTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    private SlidingLogRateLimiter Limiter(int permits, int seconds) => new(permits, TimeSpan.FromSeconds(seconds), _clock);

    private static TimeSpan RetryAfter(RateLimitLease lease)
    {
        Assert.True(lease.TryGetMetadata(MetadataName.RetryAfter, out var value));
        return value;
    }

    [Fact]
    public void It_allows_exactly_the_permit_limit_inside_one_window()
    {
        using var limiter = Limiter(3, 60);

        Assert.All(Enumerable.Range(0, 3), _ => Assert.True(limiter.AttemptAcquire().IsAcquired));
        Assert.False(limiter.AttemptAcquire().IsAcquired);
    }

    [Fact]
    public void A_refusal_reports_exactly_when_the_oldest_request_leaves_the_window()
    {
        using var limiter = Limiter(3, 60);
        limiter.AttemptAcquire();                      // t = 0
        _clock.Advance(TimeSpan.FromSeconds(10));
        limiter.AttemptAcquire();                      // t = 10
        _clock.Advance(TimeSpan.FromSeconds(10));
        limiter.AttemptAcquire();                      // t = 20
        _clock.Advance(TimeSpan.FromSeconds(10));      // сейчас t = 30

        var refused = limiter.AttemptAcquire();

        Assert.False(refused.IsAcquired);
        Assert.Equal(TimeSpan.FromSeconds(30), RetryAfter(refused));   // запрос из t = 0 выйдет из окна в t = 60
    }

    [Fact]
    public void The_window_slides_so_permits_come_back_one_by_one_and_not_all_at_once()
    {
        using var limiter = Limiter(3, 60);
        limiter.AttemptAcquire();                      // t = 0
        _clock.Advance(TimeSpan.FromSeconds(10));
        limiter.AttemptAcquire();                      // t = 10
        _clock.Advance(TimeSpan.FromSeconds(10));
        limiter.AttemptAcquire();                      // t = 20

        _clock.Advance(TimeSpan.FromSeconds(40));      // t = 60: запрос из t = 0 вышел, остальные два ещё в окне
        Assert.True(limiter.AttemptAcquire().IsAcquired);
        var refused = limiter.AttemptAcquire();
        Assert.False(refused.IsAcquired);
        Assert.Equal(TimeSpan.FromSeconds(10), RetryAfter(refused));   // следующий освободится в t = 70
    }

    [Fact]
    public void A_request_exactly_one_window_old_no_longer_counts()
    {
        using var limiter = Limiter(1, 60);
        limiter.AttemptAcquire();

        _clock.Advance(TimeSpan.FromSeconds(59.9));
        Assert.False(limiter.AttemptAcquire().IsAcquired);
        _clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(limiter.AttemptAcquire().IsAcquired);
    }

    [Fact]
    public void Refused_requests_do_not_use_up_permits_or_push_the_window_forward()
    {
        using var limiter = Limiter(1, 60);
        limiter.AttemptAcquire();                      // t = 0

        for (var i = 0; i < 20; i++)                   // настойчивые повторы в течение минуты
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            Assert.False(limiter.AttemptAcquire().IsAcquired);
        }

        _clock.Advance(TimeSpan.FromSeconds(20));      // t = 60: сам лимит не «продлился» из-за отказов
        Assert.True(limiter.AttemptAcquire().IsAcquired);
    }

    [Fact]
    public void The_retry_time_is_never_zero_or_longer_than_the_window()
    {
        using var limiter = Limiter(1, 60);
        limiter.AttemptAcquire();

        var refused = limiter.AttemptAcquire();

        Assert.InRange(RetryAfter(refused), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Several_permits_at_once_need_enough_free_places()
    {
        using var limiter = Limiter(5, 60);
        Assert.True(limiter.AttemptAcquire(3).IsAcquired);
        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(limiter.AttemptAcquire(2).IsAcquired);                  // 5 из 5

        var refused = limiter.AttemptAcquire(3);
        Assert.False(refused.IsAcquired);
        Assert.Equal(TimeSpan.FromSeconds(40), RetryAfter(refused));        // три места освободятся в t = 60

        _clock.Advance(TimeSpan.FromSeconds(40));
        Assert.True(limiter.AttemptAcquire(3).IsAcquired);
    }

    [Fact]
    public void Asking_for_more_than_the_limit_is_a_programming_error()
    {
        using var limiter = Limiter(2, 60);

        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.AttemptAcquire(3));
    }

    [Fact]
    public void Statistics_show_free_places_and_totals()
    {
        using var limiter = Limiter(2, 60);
        limiter.AttemptAcquire();
        limiter.AttemptAcquire();
        limiter.AttemptAcquire();

        var stats = limiter.GetStatistics()!;

        Assert.Equal(0, stats.CurrentAvailablePermits);
        Assert.Equal(2, stats.TotalSuccessfulLeases);
        Assert.Equal(1, stats.TotalFailedLeases);
        Assert.Equal(0, stats.CurrentQueuedCount);

        _clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(2, limiter.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public void A_limiter_is_idle_only_when_nothing_is_counted_so_the_partition_can_be_dropped()
    {
        using var limiter = Limiter(2, 60);
        limiter.AttemptAcquire();
        Assert.Null(limiter.IdleDuration);             // запрос ещё в окне: лимитер занят, удалять нельзя

        _clock.Advance(TimeSpan.FromSeconds(100));
        Assert.True(limiter.IdleDuration >= TimeSpan.FromSeconds(39));   // окно прошло: можно освобождать память
    }

    [Fact]
    public async Task The_async_path_never_queues_it_answers_at_once()
    {
        using var limiter = Limiter(1, 60);

        Assert.True((await limiter.AcquireAsync()).IsAcquired);
        Assert.False((await limiter.AcquireAsync()).IsAcquired);
    }

    [Fact]
    public void After_disposal_it_refuses_to_work()
    {
        var limiter = Limiter(1, 60);
        limiter.Dispose();

        Assert.Throws<ObjectDisposedException>(() => limiter.AttemptAcquire());
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(1, 0)]
    public void Nonsense_settings_are_refused_at_construction(int permits, int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Limiter(permits, seconds));
    }

    [Fact]
    public async Task Under_heavy_parallel_load_exactly_the_limit_gets_through()
    {
        using var limiter = Limiter(100, 60);

        var results = await Task.WhenAll(Enumerable.Range(0, 2000).Select(_ => Task.Run(() => limiter.AttemptAcquire().IsAcquired)));

        Assert.Equal(100, results.Count(r => r));
    }

    [Fact]
    public void It_works_as_a_partition_of_the_standard_partitioned_limiter()
    {
        using var partitioned = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.Get(key, _ => new SlidingLogRateLimiter(1, TimeSpan.FromMinutes(1), _clock)));

        Assert.True(partitioned.AttemptAcquire("a").IsAcquired);
        var refused = partitioned.AttemptAcquire("a");
        Assert.False(refused.IsAcquired);
        Assert.Equal(TimeSpan.FromMinutes(1), RetryAfter(refused));   // метаданные доходят и через стандартный PartitionedRateLimiter
        Assert.True(partitioned.AttemptAcquire("b").IsAcquired);      // другой ключ — другой счётчик
    }

    [Fact]
    public void It_survives_being_chained_with_another_limiter_and_reports_the_refusing_ones_time()
    {
        // Так лимитеры соединяет middleware ASP.NET: общий лимит + лимит эндпоинта.
        using var generous = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.Get(key, _ => new SlidingLogRateLimiter(100, TimeSpan.FromMinutes(1), _clock)));
        using var strict = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.Get(key, _ => new SlidingLogRateLimiter(1, TimeSpan.FromHours(1), _clock)));
        using var chained = PartitionedRateLimiter.CreateChained(generous, strict);

        Assert.True(chained.AttemptAcquire("a").IsAcquired);
        var refused = chained.AttemptAcquire("a");

        Assert.False(refused.IsAcquired);
        Assert.Equal(TimeSpan.FromHours(1), RetryAfter(refused));   // время того лимитера, который отказал, а не общего
    }
}