using Microsoft.AspNetCore.Mvc.Filters;
using SkillSwap.Application.Exceptions;

namespace SkillSwap.Api.Middleware;

/// <summary>
/// Отказывает слишком большой загрузке (413 с понятным кодом) ещё до чтения тела: по заголовку Content-Length. Без этого тело, превысившее
/// лимит, превращается в безликую ошибку разбора формы (400). Сам лимит на чтение тела (RequestSizeLimit) остаётся жёсткой защитой.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class UploadSizeLimitAttribute : Attribute, IResourceFilter
{
    private readonly long _maxBytes;
    private readonly string _code;
    private readonly string _message;

    public UploadSizeLimitAttribute(long maxBytes, string code, string message)
    {
        _maxBytes = maxBytes;
        _code = code;
        _message = message;
    }

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.HttpContext.Request.ContentLength > _maxBytes)
        {
            throw new PayloadTooLargeException(_code, _message);   // дальше его превращает в ответ общий обработчик ошибок
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
