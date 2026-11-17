using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Tests.Auth;

internal sealed class FakeMailSender : IMailSender
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

/// <summary>UserManager с подменёнными методами: настоящего хранилища в юнит-тестах нет.</summary>
internal sealed class FakeUserManager : UserManager<User>
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

    // Вход
    public string CorrectPassword { get; set; } = "abc12345";
    public bool LockedOut { get; set; }
    public int AccessFailedCalls { get; private set; }
    public int ResetFailedCalls { get; private set; }

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

    public override Task<bool> CheckPasswordAsync(User user, string password) =>
        Task.FromResult(password == CorrectPassword);

    public override Task<bool> IsLockedOutAsync(User user) => Task.FromResult(LockedOut);

    public override Task<IdentityResult> AccessFailedAsync(User user)
    {
        AccessFailedCalls++;
        return Task.FromResult(IdentityResult.Success);
    }

    public override Task<IdentityResult> ResetAccessFailedCountAsync(User user)
    {
        ResetFailedCalls++;
        return Task.FromResult(IdentityResult.Success);
    }

    public override Task<IdentityResult> UpdateAsync(User user) => Task.FromResult(IdentityResult.Success);

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

internal sealed class FakeRefreshTokenRepository : IRefreshTokenRepository
{
    public List<RefreshToken> Tokens { get; } = [];
    public int SaveCalls { get; private set; }

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct = default) =>
        Task.FromResult(Tokens.FirstOrDefault(t => t.TokenHash == tokenHash));

    public void Add(RefreshToken token) => Tokens.Add(token);

    public Task RevokeAllForUserAsync(Guid userId, DateTime nowUtc, CancellationToken ct = default)
    {
        foreach (var token in Tokens.Where(t => t.UserId == userId))
        {
            token.Revoke(nowUtc);
        }

        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default)
    {
        SaveCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeAccessTokenGenerator : IAccessTokenGenerator
{
    public AccessToken Create(User user) => new($"access-for-{user.Id}", DateTime.UtcNow.AddMinutes(15));
}

/// <summary>Собранный AuthService со всеми заглушками. «Сегодня» — 2026-06-01 12:00 UTC.</summary>
internal sealed class AuthFixture
{
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    public FakeUserManager Users { get; } = new();
    public FakeMailSender Mail { get; } = new();
    public FakeRefreshTokenRepository RefreshTokens { get; } = new();
    public AuthService Service { get; }

    public AuthFixture()
    {
        var tokens = new AuthTokenService(
            Users,
            new FakeAccessTokenGenerator(),
            RefreshTokens,
            Microsoft.Extensions.Options.Options.Create(new JwtOptions { RefreshTokenDays = 30 }),
            Clock);

        Service = new AuthService(
            Users,
            Mail,
            new RegisterRequestValidator(Clock),
            new ConfirmEmailRequestValidator(),
            new ResendConfirmationRequestValidator(),
            new LoginRequestValidator(),
            tokens,
            Clock,
            Microsoft.Extensions.Options.Options.Create(new FrontendOptions { BaseUrl = "https://app.test/" }),
            NullLogger<AuthService>.Instance);
    }
}
