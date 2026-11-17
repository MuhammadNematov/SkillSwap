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
    private readonly FakeUserManager _users = new();
    private readonly FakeMailSender _mail = new();
    private readonly AuthService _service;

    public AuthServiceTests()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        _service = new AuthService(
            _users,
            _mail,
            new RegisterRequestValidator(clock),
            new ConfirmEmailRequestValidator(),
            new ResendConfirmationRequestValidator(),
            Microsoft.Extensions.Options.Options.Create(new FrontendOptions { BaseUrl = "https://app.test/" }),
            NullLogger<AuthService>.Instance);
    }

    private static RegisterRequest ValidRequest(string? language = "ru") => new(
        "ali@example.com", "abc12345", "Ali", new DateOnly(2000, 1, 1), language, true, true);

    [Fact]
    public async Task Register_creates_user_and_sends_confirmation_link()
    {
        var response = await _service.RegisterAsync(ValidRequest(), null);

        var user = Assert.Single(_users.Created);
        Assert.Equal(response.UserId, user.Id);
        Assert.Equal("ru", user.UiLanguage);
        Assert.False(user.EmailConfirmed);

        var mail = Assert.Single(_mail.Sent);
        Assert.Equal("ali@example.com", mail.To);
        Assert.Contains($"https://app.test/confirm-email?userId={user.Id}&token=", mail.Body);
        Assert.Contains("подтвердите", mail.Subject);   // язык письма = язык пользователя
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
    public async Task ConfirmEmail_with_unknown_user_looks_like_a_bad_token()
    {
        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _service.ConfirmEmailAsync(new ConfirmEmailRequest(Guid.NewGuid(), "token")));

        Assert.Equal("invalid_confirmation_token", ex.Code);
    }

    [Fact]
    public async Task ConfirmEmail_with_bad_token_gives_the_same_error()
    {
        var user = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "en");
        _users.Existing = user;
        _users.ConfirmResult = IdentityResult.Failed(new IdentityError { Code = "InvalidToken" });

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => _service.ConfirmEmailAsync(new ConfirmEmailRequest(user.Id, "bad")));

        Assert.Equal("invalid_confirmation_token", ex.Code);
    }

    [Fact]
    public async Task ConfirmEmail_with_valid_token_succeeds()
    {
        var user = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "en");
        _users.Existing = user;

        await _service.ConfirmEmailAsync(new ConfirmEmailRequest(user.Id, "good"));

        Assert.Equal("good", _users.ConfirmedWithToken);
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
    public async Task Resend_for_unconfirmed_user_sends_mail()
    {
        _users.Existing = User.CreateRegular("ali@example.com", "Ali", new DateOnly(2000, 1, 1), "en");

        await _service.ResendConfirmationAsync(new ResendConfirmationRequest("ali@example.com"));

        Assert.Single(_mail.Sent);
    }

    // ---- заглушки ----

    private sealed class FakeMailSender : IMailSender
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = [];
        public bool Fail { get; set; }

        public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("SMTP недоступен");
            }

            Sent.Add((to, subject, body));
            return Task.CompletedTask;
        }
    }

    // UserManager с подменёнными методами: настоящего хранилища в юнит-тестах нет.
    private sealed class FakeUserManager : UserManager<User>
    {
        public FakeUserManager() : base(
            new NoopStore(), null!, null!, null!, null!, null!, null!, null!, NullLogger<UserManager<User>>.Instance)
        {
        }

        public List<User> Created { get; } = [];
        public User? Existing { get; set; }
        public IdentityResult CreateResult { get; set; } = IdentityResult.Success;
        public IdentityResult ConfirmResult { get; set; } = IdentityResult.Success;
        public string? ConfirmedWithToken { get; private set; }

        public override Task<IdentityResult> CreateAsync(User user, string password)
        {
            if (CreateResult.Succeeded)
            {
                Created.Add(user);
            }

            return Task.FromResult(CreateResult);
        }

        public override Task<string> GenerateEmailConfirmationTokenAsync(User user) => Task.FromResult("tok/en+1");

        public override Task<User?> FindByIdAsync(string userId) =>
            Task.FromResult(Existing is not null && Existing.Id.ToString() == userId ? Existing : null);

        public override Task<User?> FindByEmailAsync(string email) =>
            Task.FromResult(Existing is not null && Existing.Email == email ? Existing : null);

        public override Task<IdentityResult> ConfirmEmailAsync(User user, string token)
        {
            ConfirmedWithToken = token;
            return Task.FromResult(ConfirmResult);
        }
    }

    private sealed class NoopStore : IUserStore<User>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(User user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(User user, CancellationToken ct) => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(User user, string? userName, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken ct) => Task.CompletedTask;
        public Task<IdentityResult> CreateAsync(User user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> UpdateAsync(User user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> DeleteAsync(User user, CancellationToken ct) => Task.FromResult(IdentityResult.Success);
        public Task<User?> FindByIdAsync(string userId, CancellationToken ct) => Task.FromResult<User?>(null);
        public Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => Task.FromResult<User?>(null);
    }
}
