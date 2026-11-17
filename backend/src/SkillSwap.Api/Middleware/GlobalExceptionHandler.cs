using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Api.Middleware;

/// <summary>
/// Единое место, где исключения превращаются в ProblemDetails (RFC 7807).
/// В поле "code" лежит машинный код ошибки для перевода на фронтенде.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title, detail) = Map(exception);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Необработанная ошибка при {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
        };
        problem.Extensions["code"] = code;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem,
        });
    }

    private static (int Status, string Code, string Title, string? Detail) Map(Exception exception) => exception switch
    {
        InvalidStateTransitionException e => (StatusCodes.Status409Conflict, e.Code, "Недопустимый переход статуса", e.Message),
        // Оптимистичная блокировка по xmin (раздел 10.2 ТЗ): запись изменили параллельно.
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "concurrency_conflict", "Конфликт одновременного изменения", "Запись была изменена другим запросом. Повторите действие."),
        // Подробности внутренней ошибки клиенту не отдаём.
        _ => (StatusCodes.Status500InternalServerError, "internal_error", "Внутренняя ошибка сервера", null),
    };
}
