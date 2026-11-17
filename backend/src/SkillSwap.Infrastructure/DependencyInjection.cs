using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Skills;
using SkillSwap.Infrastructure.Profile;
using SkillSwap.Infrastructure.Skills;
using SkillSwap.Infrastructure.Seeding;
using SkillSwap.Domain.Entities;
using Microsoft.Extensions.Options;
using SkillSwap.Infrastructure.Auth;
using SkillSwap.Infrastructure.Captcha;
using SkillSwap.Infrastructure.DataProtection;
using SkillSwap.Infrastructure.LiveKit;
using SkillSwap.Infrastructure.Email;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Строка подключения читается лениво (при создании DbContext), а её наличие проверяет ConnectionStringCheck
        // при старте хоста: так не важно, каким источником и в какой момент конфигурация была дополнена.
        services.AddHostedService<ConnectionStringCheck>();
        services.AddDbContext<AppDbContext>((sp, options) => options.UseNpgsql(
            sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)));

        // Ключи шифрования (чаты, токены) лежат в БД, но только зашифрованными сертификатом, который хранится вне БД
        // (раздел 10.3 ТЗ). Без сертификата приложение не запускается.
        services.AddDataProtection()
            .SetApplicationName("SkillSwap")
            .PersistKeysToDbContext<AppDbContext>()
            .ProtectKeysWithConfiguredCertificate(configuration);

        services.AddIdentityCore<User>(options =>
            {
                // Пароль: минимум 8 символов; наличие букв и цифр проверяет валидатор регистрации.
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;

                // Токен сброса пароля выпускает отдельный поставщик со сроком 1 час (FR-4).
                options.Tokens.PasswordResetTokenProvider = PasswordResetTokenProvider.ProviderName;

                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;

                // FR-5: блокировка входа на 15 минут после 5 неудачных попыток.
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders()
            .AddTokenProvider<PasswordResetTokenProvider>(PasswordResetTokenProvider.ProviderName);

        // Первый суперадмин из переменных окружения (раздел 3 ТЗ).
        services.Configure<SuperadminOptions>(configuration.GetSection(SuperadminOptions.SectionName));
        services.AddHostedService<SuperadminSeeder>();

        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IRecoveryCodeStore, RecoveryCodeStore>();
        services.AddScoped<ITwoFactorStepStore, TwoFactorStepStore>();
        services.AddScoped<IUserActivityStore, UserActivityStore>();
        services.AddSingleton<IAccessTokenGenerator, JwtAccessTokenGenerator>();
        services.AddSingleton<ITwoFactorChallengeTokens, JwtTwoFactorChallengeTokens>();

        // Капча Cloudflare Turnstile (FR-5): настройки проверяются при старте, секрета в репозитории нет.
        services.AddOptions<TurnstileOptions>()
            .Bind(configuration.GetSection(TurnstileOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHostedService<TurnstileStartupCheck>();
        services.AddHttpClient(TurnstileCaptchaVerifier.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
                client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<TurnstileOptions>>().Value.TimeoutSeconds));
        services.AddScoped<ICaptchaVerifier, TurnstileCaptchaVerifier>();

        // Медиасервер LiveKit: токены доступа в комнату и приём webhook (раздел 7.2 ТЗ). Секрета в репозитории нет.
        services.AddOptions<LiveKitOptions>()
            .Bind(configuration.GetSection(LiveKitOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                o => string.IsNullOrEmpty(o.ApiSecret) || o.ApiSecret.Length >= LiveKitOptions.MinSecretLength,
                $"LiveKit:ApiSecret короче {LiveKitOptions.MinSecretLength} символов: такой секрет слишком слаб для подписи токенов.")
            .ValidateOnStart();
        services.AddSingleton<ILiveKitTokenService, LiveKitTokenService>();
        services.AddSingleton<ILiveKitWebhookVerifier, LiveKitWebhookVerifier>();
        services.AddScoped<ILiveKitWebhookEventStore, LiveKitWebhookEventStore>();

        // TODO: заменить на SMTP-отправитель до выхода за пределы разработки.
        services.AddScoped<IMailSender, ConsoleMailSender>();

        services.AddScoped<ISkillCatalogReader, SkillCatalogReader>();
        services.AddScoped<ISkillCatalogRepository, SkillCatalogRepository>();
        services.AddScoped<IProfileStore, ProfileStore>();
        services.AddScoped<IUserSkillStore, UserSkillStore>();

        return services;
    }
}
