using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Tests;

/// <summary>Сроки обращений (секция Cases): по умолчанию значения ТЗ, недопустимые наборы отклоняются. Общее состояние процесса (<see cref="CaseTiming"/>) здесь не меняется.</summary>
public class CaseTimingOptionsTests
{
    [Fact]
    public void Defaults_are_the_values_of_the_specification()
    {
        var options = new CaseTimingOptions();

        Assert.True(options.IsDefault);
        Assert.Null(options.Validate());
        Assert.Equal(TimeSpan.FromHours(24), options.ToSettings().HighPrioritySla);
        Assert.Equal(TimeSpan.FromHours(72), options.ToSettings().NormalPrioritySla);
        Assert.Equal(TimeSpan.FromHours(48), options.ToSettings().ExplanationWindow);
        Assert.Equal(TimeSpan.FromHours(6), options.ToSettings().ExplanationReminderBefore);
        Assert.Equal(TimeSpan.FromDays(7), options.ToSettings().AppealWindow);
        Assert.Equal(CaseTiming.Defaults, options.ToSettings());
    }

    [Fact]
    public void Shortened_values_are_valid_and_no_longer_default()
    {
        var options = new CaseTimingOptions
        {
            HighSlaMinutes = 3, NormalSlaMinutes = 5, ExplanationWindowMinutes = 4, ExplanationReminderBeforeMinutes = 2, AppealWindowMinutes = 6,
        };

        Assert.False(options.IsDefault);
        Assert.Null(options.Validate());
        Assert.Equal(TimeSpan.FromMinutes(4), options.ToSettings().ExplanationWindow);
    }

    [Theory]
    [InlineData(0, 4320, 2880, 360, 10080)]
    [InlineData(1440, 0, 2880, 360, 10080)]
    [InlineData(1440, 4320, 0, 360, 10080)]
    [InlineData(1440, 4320, 2880, 0, 10080)]
    [InlineData(1440, 4320, 2880, 360, 0)]
    [InlineData(1440, 4320, 2880, -5, 10080)]
    public void A_term_below_one_minute_is_refused(int high, int normal, int window, int reminder, int appeal)
    {
        var options = new CaseTimingOptions
        {
            HighSlaMinutes = high, NormalSlaMinutes = normal, ExplanationWindowMinutes = window, ExplanationReminderBeforeMinutes = reminder, AppealWindowMinutes = appeal,
        };

        Assert.NotNull(options.Validate());
    }

    [Theory]
    [InlineData(2880, 2880)]
    [InlineData(10, 20)]
    public void The_reminder_must_come_before_the_end_of_the_explanation_window(int window, int reminder)
    {
        var options = new CaseTimingOptions { ExplanationWindowMinutes = window, ExplanationReminderBeforeMinutes = reminder };

        var error = options.Validate();

        Assert.NotNull(error);
        Assert.Contains("ExplanationReminderBeforeMinutes", error);
    }
}
