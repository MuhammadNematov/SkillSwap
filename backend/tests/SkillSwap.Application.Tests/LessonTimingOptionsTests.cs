using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Tests;

/// <summary>Настройки сроков урока (секция Lessons): значения по умолчанию — это ТЗ, недопустимые наборы отклоняются.</summary>
public class LessonTimingOptionsTests
{
    [Fact]
    public void The_defaults_are_the_specification_values()
    {
        var options = new LessonTimingOptions();

        Assert.True(options.IsDefault);
        Assert.Null(options.Validate());
        var s = options.ToSettings();
        Assert.Equal(TimeSpan.FromHours(1), s.MinLeadTime);              // слот не раньше чем за час (6.2)
        Assert.Equal(TimeSpan.FromHours(2), s.FreeChangeWindow);         // отмена и перенос без последствий за 2 часа (6.2)
        Assert.Equal(TimeSpan.FromMinutes(10), s.JoinOpensBefore);       // вход за 10 минут (7.2)
        Assert.Equal(TimeSpan.FromMinutes(15), s.FirstCheckAfterStart);  // проверка на 15-й минуте (6.3)
        Assert.Equal(TimeSpan.FromMinutes(30), s.JoinClosesAfterEnd);    // комната закрывается через 30 минут после окончания (7.2)
        Assert.Equal(30, s.MinJointMinutes);                              // зачёт от 30 минут вместе (6.3)
        Assert.Equal(45, s.DurationMinutes);                              // урок 45 минут (6.2)
        Assert.Equal(TimeSpan.FromHours(24), s.ManualConfirmWindow);     // ручное подтверждение 24 часа (6.3)
        Assert.Equal(LessonTiming.Defaults, s);
    }

    [Fact]
    public void A_shortened_set_is_not_the_default_and_is_valid()
    {
        var options = new LessonTimingOptions
        {
            MinLeadMinutes = 1, FreeChangeMinutes = 1, JoinOpensBeforeMinutes = 3, FirstCheckMinutes = 2,
            JoinClosesAfterEndMinutes = 3, MinJointMinutes = 2, DurationMinutes = 5, ManualConfirmMinutes = 60,
        };

        Assert.False(options.IsDefault);
        Assert.Null(options.Validate());
        Assert.Equal(TimeSpan.FromMinutes(3), options.ToSettings().JoinClosesAfterEnd);
    }

    [Theory]
    [InlineData(nameof(LessonTimingOptions.MinLeadMinutes), -1)]
    [InlineData(nameof(LessonTimingOptions.FreeChangeMinutes), -5)]
    [InlineData(nameof(LessonTimingOptions.JoinOpensBeforeMinutes), -1)]
    [InlineData(nameof(LessonTimingOptions.JoinClosesAfterEndMinutes), -1)]
    [InlineData(nameof(LessonTimingOptions.ManualConfirmMinutes), 0)]
    [InlineData(nameof(LessonTimingOptions.DurationMinutes), 0)]
    [InlineData(nameof(LessonTimingOptions.DurationMinutes), 241)]
    [InlineData(nameof(LessonTimingOptions.MinJointMinutes), 0)]
    [InlineData(nameof(LessonTimingOptions.MinJointMinutes), 46)]       // больше урока: засчитать нельзя
    [InlineData(nameof(LessonTimingOptions.FirstCheckMinutes), 0)]
    [InlineData(nameof(LessonTimingOptions.FirstCheckMinutes), 76)]     // позже закрытия комнаты (45 + 30)
    public void Nonsense_values_are_rejected(string property, int value)
    {
        var options = new LessonTimingOptions();
        typeof(LessonTimingOptions).GetProperty(property)!.SetValue(options, value);

        Assert.NotNull(options.Validate());
    }

    [Fact]
    public void The_minimum_joint_time_may_equal_the_whole_lesson_and_the_first_check_may_be_the_last_minute_of_the_window()
    {
        Assert.Null(new LessonTimingOptions { MinJointMinutes = 45 }.Validate());
        Assert.Null(new LessonTimingOptions { FirstCheckMinutes = 75 }.Validate());
    }
}
