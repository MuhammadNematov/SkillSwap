using SkillSwap.Application.Matching;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Tests.Matching;

public class AgeGroupsTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    private static User Person(DateOnly birth) => User.CreateRegular("a@example.com", "A", birth, "en");

    [Theory]
    [InlineData("2008-06-15", false)]   // сегодня исполнилось 18: уже взрослый
    [InlineData("2008-06-16", true)]    // завтра исполнится 18: ещё подросток
    [InlineData("2010-01-01", true)]    // 16
    [InlineData("1990-01-01", false)]
    public void The_border_is_the_eighteenth_birthday(string birth, bool minor) =>
        Assert.Equal(minor, AgeGroups.IsMinor(DateOnly.Parse(birth), Today));

    [Fact]
    public void The_cutoff_is_exactly_eighteen_years_back() =>
        Assert.Equal(new DateOnly(2008, 6, 15), AgeGroups.AdultBirthCutoff(Today));

    [Fact]
    public void A_leap_day_birthday_does_not_break_the_border()
    {
        var onLeapDay = new DateOnly(2008, 2, 29);

        Assert.False(AgeGroups.IsMinor(onLeapDay, new DateOnly(2026, 3, 1)));   // 18 лет исполнилось 28 февраля/1 марта
        Assert.True(AgeGroups.IsMinor(onLeapDay, new DateOnly(2026, 2, 27)));
    }

    [Fact]
    public void With_the_setting_on_only_the_same_group_sees_each_other()
    {
        var teen = Person(new DateOnly(2010, 1, 1));
        var otherTeen = Person(new DateOnly(2009, 5, 5));
        var adult = Person(new DateOnly(1995, 1, 1));

        Assert.True(AgeGroups.CanSeeEachOther(teen, otherTeen, true, Today));
        Assert.False(AgeGroups.CanSeeEachOther(teen, adult, true, Today));
        Assert.False(AgeGroups.CanSeeEachOther(adult, teen, true, Today));   // и наоборот
        Assert.True(AgeGroups.CanSeeEachOther(adult, Person(new DateOnly(1980, 1, 1)), true, Today));
    }

    [Fact]
    public void With_the_setting_off_everyone_sees_everyone()
    {
        var teen = Person(new DateOnly(2010, 1, 1));
        var adult = Person(new DateOnly(1995, 1, 1));

        Assert.True(AgeGroups.CanSeeEachOther(teen, adult, false, Today));
        Assert.True(AgeGroups.CanSeeEachOther(adult, teen, false, Today));
    }

    [Fact]
    public void An_account_without_a_birth_date_never_qualifies_while_the_setting_is_on()
    {
        var staff = User.CreateStaff("s@example.com", "S", UserRole.Admin);
        var adult = Person(new DateOnly(1995, 1, 1));

        Assert.False(AgeGroups.CanSeeEachOther(staff, adult, true, Today));
        Assert.False(AgeGroups.CanSeeEachOther(adult, staff, true, Today));
    }
}
