using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;
using SkillSwap.Infrastructure.Seeding;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Auth;
using SkillSwap.Infrastructure.Email;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Не задана строка подключения ConnectionStrings:{ConnectionStringName}. " +
                "Задайте её через user-secrets или переменную окружения ConnectionStrings__Default.");

        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        // TODO (этап 2, позже): защитить ключи сертификатом вне БД (раздел 10.3 ТЗ).
        services.AddDataProtection()
            .SetApplicationName("SkillSwap")
            .PersistKeysToDbContext<AppDbContext>();

        services.AddIdentityCore<User>(options =>
            {
                // Пароль: минимум 8 символов; наличие букв и цифр проверяет валидатор регистрации.
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;

                // FR-5: блокировка входа на 15 минут после 5 неудачных попыток.
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        // Первый суперадмин из переменных окружения (раздел 3 ТЗ).
        services.Configure<SuperadminOptions>(configuration.GetSection(SuperadminOptions.SectionName));
        services.AddHostedService<SuperadminSeeder>();

        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddSingleton<IAccessTokenGenerator, JwtAccessTokenGenerator>();
        services.AddSingleton<ITwoFactorChallengeTokens, JwtTwoFactorChallengeTokens>();

        // TODO: заменить на SMTP-отправитель до выхода за пределы разработки.
        services.AddScoped<IMailSender, ConsoleMailSender>();

        return services;
    }
}
