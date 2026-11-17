using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Access-токен с нужной ролью, выпущенный настоящим генератором приложения (с настоящим ключом), без прохождения входа.
/// Нужен там, где проверяется доступ по роли: служебные аккаунты входят только через 2FA, это проверяют отдельные тесты.
/// </summary>
public static class TestTokens
{
    /// <summary>Токен для несуществующего в БД пользователя с указанной ролью (достаточно, чтобы проверить доступ по роли).</summary>
    public static string For(ApiFactory factory, UserRole role)
    {
        var user = role == UserRole.User
            ? User.CreateRegular(AuthApi.NewEmail(), "Token User", new DateOnly(1995, 5, 5), "en")
            : User.CreateStaff(AuthApi.NewEmail(), "Token Staff", role);

        return ForUser(factory, user);
    }

    public static string ForUser(ApiFactory factory, User user)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IAccessTokenGenerator>().Create(user, Guid.NewGuid()).Value;
    }
}
