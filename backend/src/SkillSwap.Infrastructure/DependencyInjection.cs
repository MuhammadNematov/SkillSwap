using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Blocks;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Dashboard;
using SkillSwap.Application.Account;
using SkillSwap.Application.Sessions;
using SkillSwap.Application.Notifications;
using SkillSwap.Infrastructure.Exchanges;
using SkillSwap.Application.Matching;
using SkillSwap.Application.Settings;
using SkillSwap.Infrastructure.Matching;
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
using SkillSwap.Infrastructure.Jobs;
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
        services.AddHostedService<FrontendUrlStartupCheck>();
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
        services.AddHttpClient(LiveKitRoomService.HttpClientName)
            .ConfigureHttpClient((sp, client) =>
                client.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<LiveKitOptions>>().Value.ApiTimeoutSeconds));
        services.AddScoped<ILiveKitRoomService, LiveKitRoomService>();
        services.AddSingleton<ILiveKitWebhookVerifier, LiveKitWebhookVerifier>();
        services.AddScoped<ILiveKitWebhookEventStore, LiveKitWebhookEventStore>();

        // Почта: приложение ставит письма в очередь (BackgroundMailSender), фоновая задача отправляет их по SMTP или,
        // пока SMTP не настроен (только Development и Testing), пишет в консоль.
        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.SectionName));
        services.AddHostedService<SmtpStartupCheck>();
        services.AddScoped<IMailTransport>(sp => sp.GetRequiredService<IOptions<SmtpOptions>>().Value.IsConfigured
            ? ActivatorUtilities.CreateInstance<SmtpMailTransport>(sp)
            : ActivatorUtilities.CreateInstance<ConsoleMailSender>(sp));
        services.AddScoped<IMailSender, BackgroundMailSender>();
        services.AddScoped<MailJobs>();

        // Файлы: закрытый бакет S3 (Garage, облачное S3 сменой настроек); пока не настроено (только Development и Testing), загрузка отвечает 503.
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName));
        services.AddHostedService<SkillSwap.Infrastructure.Storage.StorageStartupCheck>();
        services.AddSingleton<IFileStorage>(sp => sp.GetRequiredService<IOptions<StorageOptions>>().Value.IsConfigured
            ? ActivatorUtilities.CreateInstance<SkillSwap.Infrastructure.Storage.S3FileStorage>(sp)
            : new SkillSwap.Infrastructure.Storage.UnavailableFileStorage());
        services.AddHostedService<SkillSwap.Infrastructure.Storage.StorageInitializer>();
        services.AddSingleton<IImageProcessor, SkillSwap.Infrastructure.Storage.SkiaImageProcessor>();

        // Фоновые задачи (Hangfire в PostgreSQL) и транзакции с блокировкой пользователей.
        services.AddBackgroundJobs(configuration);
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        services.AddScoped<ISkillCatalogReader, SkillCatalogReader>();
        services.AddScoped<ISkillCatalogRepository, SkillCatalogRepository>();
        services.AddScoped<ISkillSuggestionStore, SkillSuggestionStore>();
        services.AddScoped<IProfileStore, ProfileStore>();
        services.AddScoped<IUserSkillStore, UserSkillStore>();
        services.AddScoped<IUserBlockStore, UserBlockStore>();
        services.AddScoped<IMatchStore, MatchStore>();
        services.AddScoped<ISystemSettingsStore, SystemSettingsStore>();
        services.AddScoped<IExchangeRequestStore, ExchangeRequestStore>();
        services.AddScoped<IExchangeStore, ExchangeStore>();
        services.AddScoped<INotificationStore, NotificationStore>();
        services.AddScoped<IMessageStore, MessageStore>();
        services.AddScoped<ILessonSessionStore, LessonSessionStore>();
        services.AddScoped<IEmailCodeStore, Auth.EmailCodeStore>();
        services.AddScoped<ISessionAttendanceStore, SessionAttendanceStore>();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddScoped<SkillSwap.Application.Moderation.IRequestInfo, Moderation.NullRequestInfo>(services);
        services.AddScoped<SkillSwap.Application.Moderation.IAuditLog, Moderation.AuditLogWriter>();
        services.AddScoped<SkillSwap.Application.Moderation.IChatAccessGrantStore, Moderation.ChatAccessGrantStore>();
        services.AddScoped<SkillSwap.Application.Reviews.IReviewStore, Moderation.ReviewStore>();
        services.AddScoped<SkillSwap.Application.Moderation.ICaseStore, Moderation.CaseStore>();
        services.AddScoped<IDashboardStore, DashboardStore>();
        services.AddScoped<IAccountStore, SkillSwap.Infrastructure.Account.AccountStore>();
        services.AddScoped<IDataExportStore, SkillSwap.Infrastructure.Account.DataExportStore>();
        services.AddScoped<IErasureHold, SkillSwap.Infrastructure.Account.NoOpenCases>();
        services.AddScoped<IUserDataExportSource, SkillSwap.Infrastructure.Account.UserDataExportSource>();
        services.AddSingleton<IMessageCipher, SkillSwap.Infrastructure.DataProtection.DataProtectionMessageCipher>();

        return services;
    }
}
