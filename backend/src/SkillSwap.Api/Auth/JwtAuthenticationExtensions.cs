using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SkillSwap.Application.Options;
using SkillSwap.Infrastructure.Auth;

namespace SkillSwap.Api.Auth;

public static class JwtAuthenticationExtensions
{
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Без ключа подписи приложение не стартует (fail fast), а не выпускает слабые токены.
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>, TimeProvider>((bearer, jwtOptions, clock) =>
            {
                var jwt = jwtOptions.Value;

                // Браузерный WebSocket не умеет слать заголовок Authorization: для хабов токен берётся из параметра access_token.
                bearer.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                };

                // Клеймы остаются под своими именами (sub, role), без переименования в длинные URI.
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    // Срок жизни проверяем по тому же TimeProvider, которым выпускаются токены (в боевой работе это
                    // системные часы): один источник времени и в коде, и в тестах с управляемыми часами.
                    LifetimeValidator = (notBefore, expires, _, _) =>
                    {
                        var now = clock.GetUtcNow().UtcDateTime;
                        var skew = TimeSpan.FromSeconds(30);
                        return (notBefore is null || notBefore.Value <= now + skew) &&
                               expires is { } exp && exp > now - skew;   // токен без exp не принимаем
                    },
                    NameClaimType = "sub",
                    RoleClaimType = JwtAccessTokenGenerator.RoleClaim,
                };
            });

        services.AddAuthorization(options => Policies.Register(options));
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, ChatAccessHandler>();
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, ActiveStaffHandler>();
        services.AddHttpContextAccessor();
        services.AddScoped<SkillSwap.Application.Moderation.IRequestInfo, HttpRequestInfo>();
        return services;
    }
}
