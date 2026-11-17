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

    [Fact]
    public void Missing_birth_date_is_required()
    {
        _validator.TestValidate(Valid() with { DateOfBirth = default })
            .ShouldHaveValidationErrorFor(x => x.DateOfBirth).WithErrorCode("required");
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
