using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

public class UserProfileTests
{
    private static User Regular() => User.CreateRegular("a@example.com", "Алиса", new DateOnly(2000, 1, 1), "ru");

    [Fact]
    public void UpdateProfile_trims_and_stores_everything()
    {
        var user = Regular();

        user.UpdateProfile("  Боб ", "  о себе ", "tg", "Asia/Dushanbe", true);

        Assert.Equal("Боб", user.Name);
        Assert.Equal("о себе", user.Bio);
        Assert.Equal("tg", user.UiLanguage);
        Assert.Equal("Asia/Dushanbe", user.TimeZone);
        Assert.True(user.IsSearchable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_bio_becomes_null(string? bio)
    {
        var user = Regular();
        user.Bio = "было";

        user.UpdateProfile("Боб", bio, "ru", "UTC", false);

        Assert.Null(user.Bio);
    }

    [Fact]
    public void Name_and_bio_have_limits_and_a_failed_update_changes_nothing()
    {
        var user = Regular();

        Assert.Equal("invalid_name", Assert.Throws<DomainValidationException>(() => user.UpdateProfile(" ", null, "tg", "UTC", true)).Code);
        Assert.Equal("invalid_name", Assert.Throws<DomainValidationException>(() => user.UpdateProfile(new string('a', User.NameMaxLength + 1), null, "tg", "UTC", true)).Code);
        Assert.Equal("invalid_bio", Assert.Throws<DomainValidationException>(() => user.UpdateProfile("Боб", new string('a', User.BioMaxLength + 1), "tg", "UTC", true)).Code);

        Assert.Equal("Алиса", user.Name);
        Assert.Equal("ru", user.UiLanguage);
        Assert.False(user.IsSearchable);

        user.UpdateProfile(new string('a', User.NameMaxLength), new string('b', User.BioMaxLength), "tg", "UTC", true);   // ровно предел — можно
    }

    [Fact]
    public void Hiding_after_being_visible_is_remembered_and_showing_again_forgets_it()
    {
        var user = Regular();
        Assert.False(user.HiddenByUser);

        user.UpdateProfile("Боб", null, "ru", "UTC", false);   // был скрыт по умолчанию и остался скрытым: это не решение
        Assert.False(user.HiddenByUser);

        user.UpdateProfile("Боб", null, "ru", "UTC", true);
        Assert.False(user.HiddenByUser);

        user.UpdateProfile("Боб", null, "ru", "UTC", false);   // был виден и скрылся сам
        Assert.True(user.HiddenByUser);

        user.UpdateProfile("Боб", null, "ru", "UTC", false);   // правка профиля без смены видимости решение не снимает
        Assert.True(user.HiddenByUser);

        user.UpdateProfile("Боб", null, "ru", "UTC", true);    // показался снова
        Assert.False(user.HiddenByUser);
    }

    [Fact]
    public void A_rejected_update_does_not_touch_the_hidden_by_user_memory()
    {
        var user = Regular();
        user.UpdateProfile("Боб", null, "ru", "UTC", true);

        Assert.Throws<DomainValidationException>(() => user.UpdateProfile(" ", null, "ru", "UTC", false));

        Assert.False(user.HiddenByUser);
        Assert.True(user.IsSearchable);
    }

    [Fact]
    public void A_staff_account_has_no_profile_to_update()
    {
        var staff = User.CreateStaff("s@example.com", "Staff", UserRole.Admin);

        Assert.False(staff.IsRegular);
        Assert.Equal("staff_has_no_profile", Assert.Throws<DomainValidationException>(() => staff.UpdateProfile("X", null, "en", "UTC", false)).Code);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(100, false)]
    public void A_user_is_a_newcomer_until_three_reviews(int reviews, bool newcomer)
    {
        var user = Regular();
        typeof(User).GetProperty(nameof(User.RatingCount))!.SetValue(user, reviews);

        Assert.Equal(newcomer, user.IsNewcomer);
    }
}
