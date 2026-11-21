using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Staff;

/// <summary>
/// Служебный аккаунт для тестов с паролем и включённой 2FA. Часы тестов заморожены, поэтому кодов приложения хватает на два действия; остальные подтверждаются
/// резервными кодами (<see cref="Confirm"/>), каждый из которых одноразовый.
/// </summary>
public sealed class StaffActor
{
    public required User User { get; init; }
    public required string Token { get; init; }
    public required string Key { get; init; }
    public required IReadOnlyList<string> Recovery { get; init; }
    public int Next;

    public Guid Id => User.Id;

    /// <summary>Подтверждение действия резервным кодом (каждый вызов берёт новый код).</summary>
    public object Confirm() => new { password = AuthApi.Password, code = Recovery[Next++], isRecoveryCode = true };

    public static async Task<StaffActor> CreateAsync(ApiFactory factory, UserRole role, string? uiLanguage = null)
    {
        var user = User.CreateStaff(AuthApi.NewEmail(), role + " (test)", role);
        user.EmailConfirmed = true;
        if (uiLanguage is not null)
        {
            user.UiLanguage = uiLanguage;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, AuthApi.Password);
            Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Code)));
        }

        var token = TestTokens.ForUser(factory, user);
        var (key, codes) = await new AuthApi(factory, TestIps.Next()).EnableTwoFactorAsync(token);
        return new StaffActor { User = user, Token = token, Key = key, Recovery = codes };
    }

    /// <summary>Обычный подтверждённый личный аккаунт с паролем (создан через Identity, как при регистрации) и его токен.</summary>
    public static async Task<(User User, string Token)> PersonalAsync(ApiFactory factory, string language = "ru", bool emailConfirmed = true)
    {
        var user = User.CreateRegular(AuthApi.NewEmail(), "Личный аккаунт", new DateOnly(1995, 1, 1), language);
        user.EmailConfirmed = emailConfirmed;
        using (var scope = factory.Services.CreateScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, AuthApi.Password)).Succeeded);
        }

        return (user, TestTokens.ForUser(factory, user));
    }
}
