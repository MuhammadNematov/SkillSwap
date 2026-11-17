using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Auth;

namespace SkillSwap.Api.RateLimiting;

/// <summary>
/// Ограничивает действие по адресу email из тела запроса (forgot-password, resend-confirmation).
/// Это фильтр действия, а не часть middleware: middleware выбирает «партицию» до чтения тела запроса,
/// а email есть только после привязки модели.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RateLimitByEmailAttribute : ServiceFilterAttribute
{
    public RateLimitByEmailAttribute() : base(typeof(EmailRateLimitFilter))
    {
    }
}

public sealed class EmailRateLimitFilter : IAsyncActionFilter
{
    private readonly EmailRateLimiter _limiter;
    private readonly RateLimitingOptions _options;

    public EmailRateLimitFilter(EmailRateLimiter limiter, IOptions<RateLimitingOptions> options)
    {
        _limiter = limiter;
        _options = options.Value;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var email = ExtractEmail(context.ActionArguments);
        if (_options.Enabled && !string.IsNullOrWhiteSpace(email) && !_limiter.TryAcquire(email, out var retryAfter))
        {
            await RateLimitResponse.WriteAsync(context.HttpContext, retryAfter, context.HttpContext.RequestAborted);
            context.Result = new EmptyResult();   // ответ уже записан
            return;
        }

        await next();
    }

    private static string? ExtractEmail(IDictionary<string, object?> arguments)
    {
        foreach (var argument in arguments.Values)
        {
            switch (argument)
            {
                case ForgotPasswordRequest forgot:
                    return forgot.Email;
                case ResendConfirmationRequest resend:
                    return resend.Email;
            }
        }

        return null;
    }
}
