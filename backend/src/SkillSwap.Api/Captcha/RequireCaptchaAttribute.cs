using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Options;

namespace SkillSwap.Api.Captcha;

/// <summary>
/// Требует пройденную капчу Cloudflare Turnstile (FR-5). Токен берётся из поля <c>captchaToken</c> тела запроса.
/// Проверка идёт ДО всей остальной логики: до валидации, поиска пользователя, проверки пароля и отправки писем.
/// Поэтому без капчи нельзя ни перебирать пароли, ни выяснять, какие email зарегистрированы.
/// <para>
/// Порядок защит: лимит по IP (middleware, он же бережёт вызовы к Cloudflare) → капча (этот фильтр) → лимит по email.
/// Капча стоит перед лимитом по email, чтобы бот не мог мусорными запросами выбрать чужую квоту писем.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireCaptchaAttribute : ServiceFilterAttribute
{
    public RequireCaptchaAttribute() : base(typeof(TurnstileFilter))
    {
        Order = -10;   // раньше фильтров по умолчанию (порядок 0), в том числе RateLimitByEmail
    }
}

public sealed class TurnstileFilter : IAsyncActionFilter
{
    private readonly ICaptchaVerifier _verifier;
    private readonly TurnstileOptions _options;

    public TurnstileFilter(ICaptchaVerifier verifier, IOptions<TurnstileOptions> options)
    {
        _verifier = verifier;
        _options = options.Value;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!_options.Enabled)
        {
            await next();
            return;
        }

        var request = context.ActionArguments.Values.OfType<ICaptchaRequest>().FirstOrDefault();
        var remoteIp = _options.SendClientIp ? context.HttpContext.Connection.RemoteIpAddress?.ToString() : null;

        var result = await _verifier.VerifyAsync(request?.CaptchaToken, remoteIp, context.HttpContext.RequestAborted);

        // Ошибки уходят в общий обработчик (ProblemDetails с кодом для перевода на фронтенде).
        switch (result.Outcome)
        {
            case CaptchaOutcome.Passed:
                await next();
                return;
            case CaptchaOutcome.Missing:
                throw new BadRequestException("captcha_required", "Подтвердите, что вы не робот.");
            case CaptchaOutcome.Invalid:
                throw new BadRequestException("captcha_invalid", "Проверка не пройдена или устарела. Пройдите её заново.");
            default:
                throw new ServiceUnavailableException(
                    "captcha_unavailable", "Не удалось проверить, что вы не робот. Попробуйте позже.");
        }
    }
}
