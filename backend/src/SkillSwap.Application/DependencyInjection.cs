using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Blocks;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Jobs;
using SkillSwap.Application.LiveKit;
using SkillSwap.Application.Matching;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Skills;

namespace SkillSwap.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

        services.AddScoped<AuthTokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<TwoFactorService>();
        services.AddScoped<PasswordResetService>();
        services.AddScoped<SessionService>();
        services.AddScoped<SkillCatalogService>();
        services.AddScoped<SkillCatalogAdminService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<MySkillsService>();
        services.AddScoped<UserBlockService>();
        services.AddScoped<MatchService>();
        services.AddScoped<ExchangeRequestService>();
        services.AddScoped<ExchangeRequestJobs>();
        services.AddScoped<SkillSuggestionService>();
        services.AddScoped<LiveKitWebhookService>();
        services.AddScoped<NoopJob>();

        return services;
    }
}
