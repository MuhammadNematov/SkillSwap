using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace SkillSwap.Api.RateLimiting;

public static class RateLimitingExtensions
{
    /// <summary>Политика для эндпоинтов, которые шлют письма (forgot-password, resend-confirmation): общий счётчик на IP.</summary>
    public const string MailPerIpPolicy = "mail-per-ip";

    /// <summary>
    /// Политика для login, login/2fa, 2fa/setup и 2fa/verify: один общий счётчик на IP
    /// (все четыре дают шанс подобрать код или пароль).
    /// </summary>
    public const string LoginPerIpPolicy = "login-per-ip";

    /// <summary>Политика для register.</summary>
    public const string RegisterPerIpPolicy = "register-per-ip";

    public const string ResetPasswordPerIpPolicy = "reset-password-per-ip";
    public const string ConfirmEmailPerIpPolicy = "confirm-email-per-ip";
    public const string RefreshPerIpPolicy = "refresh-per-ip";

    /// <summary>Предложения новых навыков: лимит на пользователя (по его id, не по IP).</summary>
    public const string SuggestionsPerUserPolicy = "suggestions-per-user";

    public static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        // Значения читаются лениво и проверяются при старте: 0 запросов или 0 секунд — ошибка конфигурации.
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<EmailRateLimiter>();
        services.AddScoped<EmailRateLimitFilter>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value)
                    ? value
                    : TimeSpan.FromMinutes(1);
                await RateLimitResponse.WriteAsync(context.HttpContext, retryAfter, ct);
            };
        });

        // Сами лимитеры собираем из настроек: так значения берутся из конфигурации, а не зашиты в код.
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitingOptions>, TimeProvider>((limiter, settings, clock) =>
        {
            var cfg = settings.Value;

            limiter.AddPolicy(MailPerIpPolicy, http =>
                SlidingLog(ClientIp.KeyFor(http), cfg.MailPerIp, clock));
            limiter.AddPolicy(LoginPerIpPolicy, http =>
                SlidingLog(ClientIp.KeyFor(http), cfg.LoginPerIp, clock));
            limiter.AddPolicy(RegisterPerIpPolicy, http =>
                SlidingLog(ClientIp.KeyFor(http), cfg.RegisterPerIp, clock));
            limiter.AddPolicy(ResetPasswordPerIpPolicy, http =>
                SlidingLog(ClientIp.KeyFor(http), cfg.ResetPasswordPerIp, clock));
            limiter.AddPolicy(ConfirmEmailPerIpPolicy, http =>
                SlidingLog(ClientIp.KeyFor(http), cfg.ConfirmEmailPerIp, clock));
            limiter.AddPolicy(RefreshPerIpPolicy, http =>
                SlidingLog(ClientIp.KeyFor(http), cfg.RefreshPerIp, clock));

            limiter.AddPolicy(SuggestionsPerUserPolicy, http =>
                SlidingLog($"suggestions:{http.User.FindFirstValue("sub") ?? ClientIp.KeyFor(http)}", cfg.SuggestionsPerUser, clock));

            // Остальные запросы авторизованного пользователя: общий лимит по пользователю (не по IP,
            // чтобы офис за одним NAT не мешал друг другу). Запросы без токена этим лимитом не считаются:
            // для них действуют только политики эндпоинтов выше.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                var userId = http.User.Identity?.IsAuthenticated == true ? http.User.FindFirstValue("sub") : null;
                return userId is null
                    ? RateLimitPartition.GetNoLimiter("anonymous")
                    : SlidingLog($"user:{userId}", cfg.AuthenticatedPerUser, clock);
            });
        });

        return services;
    }

    /// <summary>
    /// Подключает middleware лимитера, если он включён. Вызывать после UseAuthentication:
    /// глобальный лимит смотрит, кто авторизован.
    /// </summary>
    public static IApplicationBuilder UseConfiguredRateLimiter(this WebApplication app)
    {
        if (app.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value.Enabled)
        {
            app.UseRateLimiter();
        }

        return app;
    }

    private static RateLimitPartition<string> SlidingLog(string key, RateLimitRule rule, TimeProvider clock) =>
        RateLimitPartition.Get(key, _ => new SlidingLogRateLimiter(rule.PermitLimit, rule.Window, clock));
}
