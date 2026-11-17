using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Application.Auth;

namespace SkillSwap.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

        services.AddScoped<AuthService>();

        return services;
    }
}
