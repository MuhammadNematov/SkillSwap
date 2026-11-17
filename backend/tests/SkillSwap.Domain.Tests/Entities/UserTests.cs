using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Domain.Tests.Entities;

public class UserTests
{
    [Fact]
    public void CreateRegular_sets_defaults_for_a_new_user()
    {
        var user = User.CreateRegular("a@b.com", "Ali", new DateOnly(2000, 5, 10), "ru");

        Assert.NotEqual(Guid.Empty, user.Id);
        Assert.Equal("a@b.com", user.Email);
        Assert.Equal("a@b.com", user.UserName);
        Assert.Equal("Ali", user.Name);
        Assert.Equal("ru", user.UiLanguage);
        Assert.Equal(AccountType.Regular, user.AccountType);
        Assert.Equal(UserRole.User, user.Role);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.False(user.IsSearchable);
        Assert.False(user.EmailConfirmed);
    }

    [Theory]
    [InlineData("2000-05-10", "2026-05-09", 25)] // за день до дня рождения
    [InlineData("2000-05-10", "2026-05-10", 26)] // в день рождения
    [InlineData("2000-05-10", "2026-12-31", 26)]
    [InlineData("2008-02-29", "2026-02-28", 17)] // 29 февраля
    public void GetAge_counts_full_years(string birth, string today, int expected)
    {
        var user = User.CreateRegular("a@b.com", "Ali", DateOnly.Parse(birth), "en");

        Assert.Equal(expected, user.GetAge(DateOnly.Parse(today)));
    }

    [Theory]
    [InlineData("2010-01-01", "2026-06-01", true)]  // 16 лет
    [InlineData("2008-06-02", "2026-06-01", true)]  // 17 лет, до 18 один день
    [InlineData("2008-06-01", "2026-06-01", false)] // ровно 18
    public void IsMinor_is_true_below_18(string birth, string today, bool expected)
    {
        var user = User.CreateRegular("a@b.com", "Ali", DateOnly.Parse(birth), "en");

        Assert.Equal(expected, user.IsMinor(DateOnly.Parse(today)));
    }

    [Theory]
    [InlineData(UserRole.Moderator)]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.SuperAdmin)]
    public void CreateStaff_makes_a_staff_account_without_profile_and_without_2FA_yet(UserRole role)
    {
        var user = User.CreateStaff("root@b.com", "Root", role);

        Assert.Equal(AccountType.Staff, user.AccountType);
        Assert.Equal(role, user.Role);
        Assert.Equal("root@b.com", user.UserName);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Null(user.DateOfBirth);
        Assert.False(user.IsSearchable);       // в подборе служебных аккаунтов нет
        Assert.False(user.TwoFactorEnabled);   // включится при первом входе
    }

    [Fact]
    public void CreateStaff_rejects_the_plain_user_role()
    {
        Assert.Throws<ArgumentException>(() => User.CreateStaff("a@b.com", "A", UserRole.User));
    }

    [Fact]
    public void A_staff_account_has_no_age_and_is_never_a_minor()
    {
        var user = User.CreateStaff("root@b.com", "Root", UserRole.SuperAdmin);

        Assert.Null(user.GetAge(new DateOnly(2026, 6, 1)));
        Assert.False(user.IsMinor(new DateOnly(2026, 6, 1)));
    }
}
