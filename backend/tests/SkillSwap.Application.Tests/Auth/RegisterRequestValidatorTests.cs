using FluentValidation.TestHelper;
using Microsoft.Extensions.Time.Testing;
using SkillSwap.Application.Auth;

namespace SkillSwap.Application.Tests.Auth;

public class RegisterRequestValidatorTests
{
    // «Сегодня» зафиксировано: 2026-06-01 (UTC).
    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly RegisterRequestValidator _validator = new(Clock);

    private static RegisterRequest Valid() => new(
        "ali@example.com", "abc12345", "Ali", new DateOnly(2000, 1, 1), "ru", true, true);

    [Fact]
    public void Valid_request_passes()
    {
        _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("not-an-email", "invalid_email")]
    public void Email_is_checked(string email, string code)
    {
        _validator.TestValidate(Valid() with { Email = email })
            .ShouldHaveValidationErrorFor(x => x.Email).WithErrorCode(code);
    }

    [Theory]
    [InlineData("abc1234", "password_too_short")]     // 7 символов
    [InlineData("abcdefgh", "password_needs_digit")]
    [InlineData("12345678", "password_needs_letter")]
    public void Password_needs_8_chars_letters_and_digits(string password, string code)
    {
        _validator.TestValidate(Valid() with { Password = password })
            .ShouldHaveValidationErrorFor(x => x.Password).WithErrorCode(code);
    }

    [Theory]
    [InlineData("2010-06-01", false)]  // ровно 16 лет сегодня — можно
    [InlineData("2010-06-02", true)]   // 16 исполнится завтра — нельзя
    [InlineData("2020-01-01", true)]
    public void Under_16_is_rejected(string birth, bool rejected)
    {
        var result = _validator.TestValidate(Valid() with { DateOfBirth = DateOnly.Parse(birth) });

        if (rejected)
        {
            result.ShouldHaveValidationErrorFor(x => x.DateOfBirth).WithErrorCode("too_young");
        }
        else
        {
            result.ShouldNotHaveValidationErrorFor(x => x.DateOfBirth);
        }
    }

    [Fact]
    public void Birth_date_in_future_is_rejected()
    {
        _validator.TestValidate(Valid() with { DateOfBirth = new DateOnly(2027, 1, 1) })
            .ShouldHaveValidationErrorFor(x => x.DateOfBirth).WithErrorCode("date_in_future");
    }

    // Год не раньше 1900 (поле date в браузере позволяет набрать 0007: возраст тогда около 2000 лет).
    [Theory]
    [InlineData("0007-05-05")]
    [InlineData("0002-01-01")]
    [InlineData("1899-12-31")]
    public void A_birth_year_before_1900_is_rejected(string birth)
    {
        var result = _validator.TestValidate(Valid() with { DateOfBirth = DateOnly.Parse(birth) });

        result.ShouldHaveValidationErrorFor(x => x.DateOfBirth).WithErrorCode("date_too_early");
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "too_young");   // это не «слишком молод»: причина названа точно
    }

    [Theory]
    [InlineData("1900-01-01")]   // граница включена
    [InlineData("1950-06-15")]
    [InlineData("2010-06-01")]   // ровно 16 сегодня
    public void Birth_years_from_1900_on_are_accepted(string birth)
    {
        _validator.TestValidate(Valid() with { DateOfBirth = DateOnly.Parse(birth) })
            .ShouldNotHaveValidationErrorFor(x => x.DateOfBirth);
    }

    [Fact]
    public void A_modern_too_young_date_does_not_get_the_early_year_error()
    {
        var result = _validator.TestValidate(Valid() with { DateOfBirth = new DateOnly(2020, 1, 1) });

        Assert.Contains(result.Errors, e => e.ErrorCode == "too_young");
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "date_too_early");
    }

    [Fact]
    public void Missing_birth_date_is_required()
    {
        var result = _validator.TestValidate(Valid() with { DateOfBirth = default });

        result.ShouldHaveValidationErrorFor(x => x.DateOfBirth).WithErrorCode("required");
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "date_too_early");   // 0001-01-01 это «не указано», а не «слишком давно»
    }

    [Fact]
    public void Unsupported_language_is_rejected_but_null_is_allowed()
    {
        _validator.TestValidate(Valid() with { UiLanguage = "de" })
            .ShouldHaveValidationErrorFor(x => x.UiLanguage).WithErrorCode("unsupported_language");
        _validator.TestValidate(Valid() with { UiLanguage = null })
            .ShouldNotHaveValidationErrorFor(x => x.UiLanguage);
    }

    [Fact]
    public void Both_consents_are_required()
    {
        var result = _validator.TestValidate(Valid() with { AcceptTerms = false, AcceptChatModeration = false });

        result.ShouldHaveValidationErrorFor(x => x.AcceptTerms).WithErrorCode("terms_not_accepted");
        result.ShouldHaveValidationErrorFor(x => x.AcceptChatModeration).WithErrorCode("chat_moderation_not_accepted");
    }
}
