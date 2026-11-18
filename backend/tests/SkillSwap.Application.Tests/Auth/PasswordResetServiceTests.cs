using FluentValidation;
using Microsoft.AspNetCore.Identity;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Tests.Auth;

public class PasswordResetServiceTests
{
    private static readonly ClientInfo Client = new("Firefox", "127.0.0.1");

    private readonly AuthFixture _fx = new();

    private User AddUser(bool confirmed = true, string language = "ru")
    {
        var user = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), language);
        user.EmailConfirmed = confirmed;
        _fx.Users.Existing = user;
        return user;
    }

    private Task Reset(User user, string token = "cmVzZXQtdG9rZW4=", string password = "newpass123") =>
        _fx.PasswordReset.ResetPasswordAsync(new ResetPasswordRequest(user.Id, token, password));

    // ---------- запрос сброса ----------

    [Fact]
    public async Task Forgot_sends_a_link_with_user_id_and_token_in_the_users_language()
    {
        var user = AddUser(language: "ru");

        await _fx.PasswordReset.ForgotPasswordAsync(new ForgotPasswordRequest("ali@example.com"));

        var mail = Assert.Single(_fx.Mail.Sent);
        Assert.Equal("ali@example.com", mail.To);
        Assert.Contains("сброс пароля", mail.Subject);
        Assert.Contains($"https://app.test/reset-password?userId={user.Id}&token=cmVzZXQtdG9rZW4", mail.Body);
        Assert.Contains("1 час", mail.Body);
    }

    [Fact]
    public async Task Forgot_puts_a_url_safe_token_in_the_link()
    {
        var user = AddUser();
        _fx.Users.ResetToken = "a/b+c=";

        await _fx.PasswordReset.ForgotPasswordAsync(new ForgotPasswordRequest("ali@example.com"));

        Assert.Contains($"userId={user.Id}&token=a_b-c", Assert.Single(_fx.Mail.Sent).Body);   // «/»→«_», «+»→«-», «=» убран
    }

    [Fact]
    public async Task Forgot_for_an_unknown_email_is_silent_and_sends_nothing()
    {
        await _fx.PasswordReset.ForgotPasswordAsync(new ForgotPasswordRequest("nobody@example.com"));

        Assert.Empty(_fx.Mail.Sent);
    }

    [Fact]
    public async Task Forgot_for_an_unconfirmed_account_sends_nothing()
    {
        AddUser(confirmed: false);

        await _fx.PasswordReset.ForgotPasswordAsync(new ForgotPasswordRequest("ali@example.com"));

        Assert.Empty(_fx.Mail.Sent);
    }

    [Theory]
    [InlineData(UserStatus.Blocked)]
    [InlineData(UserStatus.Deleted)]
    public async Task Forgot_for_a_blocked_or_deleted_account_sends_nothing(UserStatus status)
    {
        var user = AddUser();
        typeof(User).GetProperty(nameof(User.Status))!.SetValue(user, status);

        await _fx.PasswordReset.ForgotPasswordAsync(new ForgotPasswordRequest("ali@example.com"));

        Assert.Empty(_fx.Mail.Sent);
    }

    [Fact]
    public async Task Forgot_behaves_the_same_when_the_mail_server_is_down()
    {
        AddUser();
        _fx.Mail.Fail = true;

        // Ошибка отправки не должна быть видна снаружи: иначе по ней отличим существующий email от неизвестного.
        await _fx.PasswordReset.ForgotPasswordAsync(new ForgotPasswordRequest("ali@example.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task Forgot_rejects_a_malformed_email(string email)
    {
        await Assert.ThrowsAsync<ValidationException>(
            () => _fx.PasswordReset.ForgotPasswordAsync(new ForgotPasswordRequest(email)));
    }

    // ---------- сам сброс ----------

    [Fact]
    public async Task Reset_sets_the_new_password()
    {
        var user = AddUser();

        await Reset(user);

        Assert.Equal("newpass123", _fx.Users.CorrectPassword);
        Assert.True(_fx.Users.ResetTokenConsumed);
    }

    [Fact]
    public async Task Reset_revokes_all_refresh_tokens_of_the_user_and_only_theirs()
    {
        var user = AddUser();
        var other = User.CreateRegular("bob@example.com", "Bob", new DateOnly(2000, 1, 1), "en");
        var tokensService = _fx.RefreshTokens;
        await _fx.Service.LoginAsync(new LoginRequest("ali@example.com", "abc12345"), Client);
        await _fx.Service.LoginAsync(new LoginRequest("ali@example.com", "abc12345"), Client);   // второе устройство
        tokensService.Add(RefreshToken.Create(other.Id, "bobs-hash", DateTime.UtcNow.AddDays(1), null, null, Guid.NewGuid(), DateTime.UtcNow));

        await Reset(user);

        Assert.All(tokensService.Tokens.Where(t => t.UserId == user.Id), t => Assert.True(t.IsRevoked));
        Assert.Equal(2, tokensService.Tokens.Count(t => t.UserId == user.Id));
        Assert.False(tokensService.Tokens.Single(t => t.UserId == other.Id).IsRevoked);   // чужие сеансы не тронуты
    }

    [Fact]
    public async Task Reset_clears_the_lockout_caused_by_failed_logins()
    {
        var user = AddUser();
        _fx.Users.LockedOut = true;

        await Reset(user);

        Assert.False(_fx.Users.LockedOut);
        Assert.Equal(1, _fx.Users.LockoutEndClearedCalls);
        Assert.Equal(1, _fx.Users.ResetFailedCalls);
    }

    [Fact]
    public async Task Reset_does_not_turn_off_2FA()
    {
        var user = AddUser();
        user.TwoFactorEnabled = true;

        await Reset(user);

        Assert.True(user.TwoFactorEnabled);   // новый пароль не отменяет второй фактор
    }

    [Fact]
    public async Task Reset_notifies_the_owner_by_email()
    {
        var user = AddUser(language: "ru");

        await Reset(user);

        var mail = Assert.Single(_fx.Mail.Sent);
        Assert.Equal("ali@example.com", mail.To);
        Assert.Contains("пароль изменён", mail.Subject);
    }

    [Fact]
    public async Task Reset_succeeds_even_if_the_notification_cannot_be_sent()
    {
        var user = AddUser();
        _fx.Mail.Fail = true;

        await Reset(user);

        Assert.Equal("newpass123", _fx.Users.CorrectPassword);
    }

    [Fact]
    public async Task Reset_token_works_only_once()
    {
        var user = AddUser();
        await Reset(user);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => Reset(user, password: "another123"));

        Assert.Equal("invalid_reset_token", ex.Code);
        Assert.Equal("newpass123", _fx.Users.CorrectPassword);
    }

    [Fact]
    public async Task Reset_with_a_wrong_token_is_400_and_changes_nothing()
    {
        var user = AddUser();
        var tokensService = _fx.RefreshTokens;
        await _fx.Service.LoginAsync(new LoginRequest("ali@example.com", "abc12345"), Client);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => Reset(user, token: "wrong"));

        Assert.Equal("invalid_reset_token", ex.Code);
        Assert.Equal("abc12345", _fx.Users.CorrectPassword);
        Assert.All(tokensService.Tokens, t => Assert.False(t.IsRevoked));   // сеансы целы
        Assert.Empty(_fx.Mail.Sent);
    }

    [Fact]
    public async Task Reset_for_an_unknown_user_gives_the_same_error_as_a_wrong_token()
    {
        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _fx.PasswordReset.ResetPasswordAsync(new ResetPasswordRequest(Guid.NewGuid(), "cmVzZXQtdG9rZW4=", "newpass123")));

        Assert.Equal("invalid_reset_token", ex.Code);
    }

    [Theory]
    [InlineData(UserStatus.Blocked)]
    [InlineData(UserStatus.Deleted)]
    public async Task Reset_for_a_blocked_or_deleted_user_gives_the_same_error(UserStatus status)
    {
        var user = AddUser();
        typeof(User).GetProperty(nameof(User.Status))!.SetValue(user, status);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => Reset(user));

        Assert.Equal("invalid_reset_token", ex.Code);
    }

    [Theory]
    [InlineData("short1", "password_too_short")]
    [InlineData("onlyletters", "password_needs_digit")]
    [InlineData("12345678", "password_needs_letter")]
    public async Task A_weak_password_is_rejected_before_the_token_is_spent(string password, string code)
    {
        var user = AddUser();

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Reset(user, password: password));

        Assert.Contains(ex.Errors, e => e.PropertyName == "NewPassword" && e.ErrorCode == code);
        Assert.False(_fx.Users.ResetTokenConsumed);   // ссылка не «сгорела»
        await Reset(user, password: "goodpass123");   // и по ней всё ещё можно сбросить
        Assert.Equal("goodpass123", _fx.Users.CorrectPassword);
    }

    [Fact]
    public async Task A_password_policy_failure_from_Identity_is_400_password_rejected()
    {
        var user = AddUser();
        _fx.Users.ForcedResetResult = IdentityResult.Failed(new IdentityError { Code = "PasswordRequiresUpper" });

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => Reset(user));

        Assert.Equal("password_rejected", ex.Code);
    }

    [Fact]
    public async Task A_concurrency_failure_is_409()
    {
        var user = AddUser();
        _fx.Users.ForcedResetResult = IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure" });

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Reset(user));

        Assert.Equal("concurrency_conflict", ex.Code);
    }

    [Fact]
    public async Task A_failed_reset_does_not_revoke_sessions_or_clear_the_lockout()
    {
        var user = AddUser();
        _fx.Users.LockedOut = true;
        _fx.RefreshTokens.Add(RefreshToken.Create(user.Id, "h", DateTime.UtcNow.AddDays(1), null, null, Guid.NewGuid(), DateTime.UtcNow));

        await Assert.ThrowsAsync<BadRequestException>(() => Reset(user, token: "wrong"));

        Assert.True(_fx.Users.LockedOut);
        Assert.False(_fx.RefreshTokens.Tokens.Single().IsRevoked);
    }
}
