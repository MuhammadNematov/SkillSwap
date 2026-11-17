using System.Globalization;
using Microsoft.AspNetCore.Mvc;

namespace SkillSwap.Api.RateLimiting;

/// <summary>
/// Единый ответ 429 для всех лимитов (и для middleware, и для лимита по email):
/// ProblemDetails с кодом <c>rate_limited</c> и заголовок <c>Retry-After</c> (целые секунды).
/// </summary>
public static class RateLimitResponse
{
    public const string Code = "rate_limited";

    public static async ValueTask WriteAsync(HttpContext http, TimeSpan retryAfter, CancellationToken ct = default)
    {
        var seconds = (int)Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds));

        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Слишком много запросов",
            Detail = $"Лимит запросов превышен. Повторите через {seconds} с.",
        };
        problem.Extensions["code"] = Code;
        problem.Extensions["retryAfterSeconds"] = seconds;

        var writer = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        var written = await writer.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
        if (!written)
        {
            await http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", ct);
        }

        // Без персональных данных: ни IP, ни email в журнал не пишем.
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SkillSwap.RateLimiting")
            .LogInformation("Лимит запросов превышен: {Method} {Path}, повтор через {Seconds} с",
                http.Request.Method, http.Request.Path.Value, seconds);
    }
}
