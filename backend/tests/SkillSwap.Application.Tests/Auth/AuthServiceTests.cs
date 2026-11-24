using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Tests.Auth;

public class AuthServiceTests
{
    private readonly AuthFixture _fx = new();
    private FakeUserManager _users => _fx.Users;
    private FakeMailSender _mail => _fx.Mail;
    private AuthService _service => _fx.Service;

    private static RegisterRequest ValidRequest(string? language = "ru") => new(
        "ali@example.com", "abc12345", "Ali", new DateOnly(2000, 1, 1), language, true, true);

    [Fact]
    public async Task Register_creates_user_and_sends_a_confirmation_code_without_a_link()
    {
        var response = await _service.RegisterAsync(ValidRequest(), null);

        var user = Assert.Single(_users.Created);
        Assert.Equal(response.UserId, user.Id);
        Assert.Equal("ru", user.UiLanguage);
        Assert.False(user.EmailConfirmed);

        var mail = Assert.Single(_mail.Sent);
        Assert.Equal("ali@example.com", mail.To);
        Assert.Matches(@"\b\d{6}\b", mail.Body);                 // шестизначный код
        Assert.DoesNotContain("confirm-email", mail.Body);           // ссылки больше нет
        Assert.DoesNotContain("http", mail.Body);
        Assert.Contains("код подтверждения", mail.Subject);          // язык письма = язык пользователя
    }

    [Fact]
    public async Task Register_takes_language_from_Accept_Language_when_not_specified()
    {
        await _service.RegisterAsync(ValidRequest(language: null), "tg-TJ,tg;q=0.9,ru;q=0.5");

        Assert.Equal("tg", Assert.Single(_users.Created).UiLanguage);
    }

    [Fact]
    public async Task Register_duplicate_email_gives_conflict()
    {
        _users.CreateResult = IdentityResult.Failed(new IdentityError { Code = "DuplicateEmail" });

        var ex = await Assert.ThrowsAsync<ConflictException>(() => _service.RegisterAsync(ValidRequest(), null));

        Assert.Equal("email_already_registered", ex.Code);
        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task Register_invalid_request_throws_validation_error_before_touching_the_store()
    {
        await Assert.ThrowsAsync<ValidationException>(
            () => _service.RegisterAsync(ValidRequest() with { Password = "short" }, null));

        Assert.Empty(_users.Created);
    }

    [Fact]
    public async Task Register_succeeds_even_if_mail_sending_fails()
    {
        _mail.Fail = true;

        var response = await _service.RegisterAsync(ValidRequest(), null);

        Assert.Equal("ali@example.com", response.Email);
        Assert.Single(_users.Created);
    }

    [Fact]
    public async Task Resend_for_unknown_email_is_silent()
    {
        await _service.ResendConfirmationAsync(new ResendConfirmationRequest("nobody@example.com"));

        Assert.Empty(_mail.Sent);
    }

    [Fact]
    public async Task Resend_for_already_confirmed_user_is_silent()
    {
        var user = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "en");
        user.EmailConfirmed = true;
        _users.Existing = user;

        await _service.ResendConfirmationAsync(new ResendConfirmationRequest("ali@example.com"));

        Assert.Empty(_mail.Sent);
    }


    [Fact]
    public async Task Resend_queues_one_empty_job_when_there_is_no_letter_to_send_and_none_when_there_is()
    {
        await _service.ResendConfirmationAsync(new ResendConfirmationRequest("nobody@example.com"));            // неизвестный
        var confirmed = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "en");
        confirmed.EmailConfirmed = true;
        _users.Existing = confirmed;
        await _service.ResendConfirmationAsync(new ResendConfirmationRequest("ali@example.com"));               // уже подтверждён
        Assert.Equal(2, _fx.Jobs.NoopCount);
        Assert.Empty(_mail.Sent);

        _users.Existing = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "en");
        await _service.ResendConfirmationAsync(new ResendConfirmationRequest("ali@example.com"));               // настоящее письмо
        Assert.Single(_mail.Sent);
        Assert.Equal(2, _fx.Jobs.NoopCount);                                                                    // пустых задач не прибавилось
    }
    [Fact]
    public async Task Resend_for_unconfirmed_user_sends_mail()
    {
        _users.Existing = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "en");

        await _service.ResendConfirmationAsync(new ResendConfirmationRequest("ali@example.com"));

        Assert.Single(_mail.Sent);
    }
}
