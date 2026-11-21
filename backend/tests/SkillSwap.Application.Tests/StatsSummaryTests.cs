using SkillSwap.Application.Stats;

namespace SkillSwap.Application.Tests;

/// <summary>Сводка наблюдений времени реакции: среднее, медиана и доля уложившихся в срок.</summary>
public class StatsSummaryTests
{
    private static ReactionSample Hours(double hours, bool within = true) => new(hours * 3600, within);

    [Fact]
    public void No_samples_give_no_numbers()
    {
        var summary = StatsService.Summarize([]);

        Assert.Equal(0, summary.CasesTaken);
        Assert.Null(summary.AverageHours);
        Assert.Null(summary.MedianHours);
        Assert.Null(summary.WithinSlaPercent);
    }

    [Fact]
    public void A_single_sample_is_its_own_average_and_median()
    {
        var summary = StatsService.Summarize([Hours(5)]);

        Assert.Equal(1, summary.CasesTaken);
        Assert.Equal(5, summary.AverageHours);
        Assert.Equal(5, summary.MedianHours);
        Assert.Equal(100, summary.WithinSlaPercent);
    }

    [Fact]
    public void The_median_of_an_even_count_is_the_mean_of_the_middle_pair_and_the_average_differs_from_it()
    {
        var summary = StatsService.Summarize([Hours(1), Hours(2), Hours(3), Hours(14, within: false)]);

        Assert.Equal(5, summary.AverageHours);
        Assert.Equal(2.5, summary.MedianHours);
        Assert.Equal(75, summary.WithinSlaPercent);
    }

    [Fact]
    public void The_median_of_an_odd_count_is_the_middle_value_whatever_the_order()
    {
        var summary = StatsService.Summarize([Hours(30, within: false), Hours(1), Hours(4)]);

        Assert.Equal(4, summary.MedianHours);
        Assert.Equal(11.67, summary.AverageHours);
        Assert.Equal(66.7, summary.WithinSlaPercent);
    }

    [Fact]
    public void A_negative_time_from_clock_drift_counts_as_zero()
    {
        var summary = StatsService.Summarize([new ReactionSample(-7200, true), Hours(2)]);

        Assert.Equal(1, summary.AverageHours);
        Assert.Equal(1, summary.MedianHours);
    }
}
