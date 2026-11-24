using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Exceptions;

namespace SkillSwap.Application.Tests.Auth;

/// <summary>Шестизначный код подтверждения email: выпуск, письмо, проверка, попытки, срок.</summary>
public class EmailConfirmationCodeTests
{
    private readonly AuthFixture _fx = new();

    private static RegisterRequest Request() => new("ali@example.com", "abc12345", "Ali", new DateOnly(2000, 1, 1), "ru", true, true);

    /// <summary>Код в письме стоит в конце строки после двоеточия («…на сайте: 123456»); ссылка и токен под это не попадают.</summary>
    private static string LetterCode(string body) => Regex.Match(body, @"(?m)^\s*(\d{6})\s*$").Groups[1].Value;   // код стоит отдельной строкой

    private string CodeFromLetter(int index = -1)
    {
        var mail = _fx.Mail.Sent[index < 0 ? _fx.Mail.Sent.Count - 1 : index];
        return LetterCode(mail.Body);
    }

    private async Task RegisterAsync()
    {
        await _fx.Service.RegisterAsync(Request(), null);
        _fx.Users.Existing = _fx.Users.Created[0];
    }

    private Task Confirm(string code, string email = "ali@example.com") =>
        _fx.Service.ConfirmEmailByCodeAsync(new ConfirmEmailCodeRequest(email, code));

    private static async Task<string> CodeOf(Task task) => (await Assert.ThrowsAsync<BadRequestException>(() => task)).Code;

    // ---------- сам код ----------

    [Fact]
    public void Generated_codes_are_six_digits_with_leading_zeros_kept()
    {
        for (var i = 0; i < 500; i++)
        {
            Assert.Matches(@"^\d{6}$", EmailConfirmationCodes.Generate());
        }
    }

    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123 456 ", "123456")]
    [InlineData("123-456", "123456")]
    [InlineData("004217", "004217")]
    [InlineData("12345", null)]
    [InlineData("1234567", null)]
    [InlineData("12a456", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Pasted_codes_are_cleaned_and_everything_else_is_rejected(string? input, string? expected) =>
        Assert.Equal(expected, EmailConfirmationCodes.Normalize(input));

    [Fact]
    public void The_hash_depends_on_the_user_and_matching_is_exact()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var hash = EmailConfirmationCodes.Hash(a, "123456");

        Assert.NotEqual(hash, EmailConfirmationCodes.Hash(b, "123456"));
        Assert.True(EmailConfirmationCodes.Matches(a, "123456", hash));
        Assert.False(EmailConfirmationCodes.Matches(a, "123457", hash));
        Assert.False(EmailConfirmationCodes.Matches(b, "123456", hash));
    }

    // ---------- письмо ----------

    [Fact]
    public async Task The_registration_letter_has_only_the_code_and_the_code_is_stored_only_as_a_hash()
    {
        await RegisterAsync();

        var body = Assert.Single(_fx.Mail.Sent).Body;
        var code = CodeFromLetter();
        Assert.Contains(code, body);
        Assert.DoesNotContain("confirm-email", body);   // ссылки нет: подтверждение только кодом
        Assert.Contains("15 минут", body);
        var stored = Assert.Single(_fx.EmailCodes.Codes.Values);
        Assert.DoesNotContain(code, stored.Hash);
        Assert.Equal(_fx.Clock.GetUtcNow().UtcDateTime.AddMinutes(15), stored.ExpiresAt);
    }

    [Theory]
    [InlineData("tg", "Рамзи тасдиқи шумо")]
    [InlineData("en", "Your confirmation code")]
    public async Task The_code_text_follows_the_interface_language(string language, string fragment)
    {
        await _fx.Service.RegisterAsync(Request() with { UiLanguage = language }, null);

        Assert.Contains(fragment, _fx.Mail.Sent[0].Body);
        Assert.Matches(@"\d{6}", _fx.Mail.Sent[0].Body);
    }

    // ---------- подтверждение ----------

    [Fact]
    public async Task The_right_code_confirms_the_email_and_is_used_up()
    {
        await RegisterAsync();
        var code = CodeFromLetter();

        await Confirm(code);

        Assert.NotNull(_fx.Users.ConfirmedWithToken);
        Assert.Empty(_fx.EmailCodes.Codes);
    }

    [Fact]
    public async Task A_code_with_a_space_in_the_middle_works()
    {
        await RegisterAsync();
        var code = CodeFromLetter();

        await Confirm($"{code[..3]} {code[3..]}");

        Assert.NotNull(_fx.Users.ConfirmedWithToken);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_costs_an_attempt()
    {
        await RegisterAsync();
        var wrong = CodeFromLetter() == "000000" ? "000001" : "000000";

        Assert.Equal("invalid_confirmation_code", await CodeOf(Confirm(wrong)));

        Assert.Null(_fx.Users.ConfirmedWithToken);
        Assert.Equal(1, Assert.Single(_fx.EmailCodes.Codes.Values).Attempts);
    }

    [Fact]
    public async Task After_five_wrong_tries_even_the_right_code_stops_working()
    {
        await RegisterAsync();
        var right = CodeFromLetter();
        var wrong = right == "000000" ? "000001" : "000000";

        for (var i = 0; i < EmailConfirmationCodes.MaxAttempts; i++)
        {
            Assert.Equal("invalid_confirmation_code", await CodeOf(Confirm(wrong)));
        }

        Assert.Equal("invalid_confirmation_code", await CodeOf(Confirm(right)));
        Assert.Null(_fx.Users.ConfirmedWithToken);
    }

    [Fact]
    public async Task The_last_allowed_try_can_still_be_the_right_one()
    {
        await RegisterAsync();
        var right = CodeFromLetter();
        var wrong = right == "000000" ? "000001" : "000000";
        for (var i = 0; i < EmailConfirmationCodes.MaxAttempts - 1; i++)
        {
            await CodeOf(Confirm(wrong));
        }

        await Confirm(right);

        Assert.NotNull(_fx.Users.ConfirmedWithToken);
    }

    private static async Task<(AuthFixture Fx, string Code)> FreshAsync()
    {
        var fx = new AuthFixture();
        await fx.Service.RegisterAsync(Request(), null);
        fx.Users.Existing = fx.Users.Created[0];
        return (fx, LetterCode(fx.Mail.Sent[0].Body));
    }

    [Fact]
    public async Task The_code_still_works_one_tick_before_fifteen_minutes()
    {
        var (fx, code) = await FreshAsync();
        fx.Clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1));

        await fx.Service.ConfirmEmailByCodeAsync(new ConfirmEmailCodeRequest("ali@example.com", code));

        Assert.NotNull(fx.Users.ConfirmedWithToken);
    }

    [Fact]
    public async Task The_code_is_dead_at_exactly_fifteen_minutes()
    {
        var (fx, code) = await FreshAsync();
        fx.Clock.Advance(TimeSpan.FromMinutes(15));

        var error = await Assert.ThrowsAsync<BadRequestException>(
            () => fx.Service.ConfirmEmailByCodeAsync(new ConfirmEmailCodeRequest("ali@example.com", code)));

        Assert.Equal("invalid_confirmation_code", error.Code);
        Assert.Null(fx.Users.ConfirmedWithToken);
    }

    [Fact]
    public async Task A_new_letter_cancels_the_old_code_and_resets_the_attempts()
    {
        await RegisterAsync();
        var old = CodeFromLetter();
        var wrong = old == "000000" ? "000001" : "000000";
        await CodeOf(Confirm(wrong));

        await _fx.Service.ResendConfirmationAsync(new ResendConfirmationRequest("ali@example.com"));
        var fresh = CodeFromLetter();

        Assert.Equal(0, Assert.Single(_fx.EmailCodes.Codes.Values).Attempts);
        if (fresh != old)
        {
            Assert.Equal("invalid_confirmation_code", await CodeOf(Confirm(old)));
        }

        await Confirm(fresh);
        Assert.NotNull(_fx.Users.ConfirmedWithToken);
    }

    // ---------- что не раскрываем ----------

    [Fact]
    public async Task An_unknown_address_looks_like_a_wrong_code_and_no_attempt_is_spent_anywhere()
    {
        await RegisterAsync();

        Assert.Equal("invalid_confirmation_code", await CodeOf(Confirm("123456", "nobody@example.com")));

        Assert.Equal(0, Assert.Single(_fx.EmailCodes.Codes.Values).Attempts);
    }

    [Fact]
    public async Task An_already_confirmed_address_looks_like_a_wrong_code()
    {
        await RegisterAsync();
        var code = CodeFromLetter();
        _fx.Users.Created[0].EmailConfirmed = true;

        Assert.Equal("invalid_confirmation_code", await CodeOf(Confirm(code)));
    }

    [Theory]
    [InlineData("", "123456")]
    [InlineData("ali@example.com", "")]
    [InlineData("ali@example.com", "12345")]
    [InlineData("ali@example.com", "abcdef")]
    public async Task A_request_of_the_wrong_shape_is_a_validation_error(string email, string code)
    {
        await RegisterAsync();

        await Assert.ThrowsAsync<ValidationException>(() => Confirm(code, email));

        Assert.Equal(0, Assert.Single(_fx.EmailCodes.Codes.Values).Attempts);   // до попыток дело не дошло
    }

    [Fact]
    public async Task If_sending_the_letter_fails_registration_still_works_and_the_code_can_be_requested_again()
    {
        _fx.Mail.Fail = true;

        await _fx.Service.RegisterAsync(Request(), null);

        Assert.Single(_fx.Users.Created);
        _fx.Mail.Fail = false;
        _fx.Users.Existing = _fx.Users.Created[0];
        await _fx.Service.ResendConfirmationAsync(new ResendConfirmationRequest("ali@example.com"));
        await Confirm(CodeFromLetter());
        Assert.NotNull(_fx.Users.ConfirmedWithToken);
    }
}
