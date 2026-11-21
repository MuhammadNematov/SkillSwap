namespace SkillSwap.Application.Exceptions;

/// <summary>
/// Ожидаемая ошибка сервиса. Code — машинный код для перевода на фронтенде.
/// Глобальный обработчик превращает её в ProblemDetails.
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) : base(message)
    {
        Code = code;
        Extensions = extensions;
    }

    public string Code { get; }

    /// <summary>Дополнительные поля ответа ProblemDetails (например, id найденного навыка), ключи в camelCase.</summary>
    public IReadOnlyDictionary<string, object?>? Extensions { get; }
}

/// <summary>HTTP 400: запрос некорректен.</summary>
public sealed class BadRequestException : AppException
{
    public BadRequestException(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) : base(code, message, extensions)
    {
    }
}

/// <summary>HTTP 401: не удалось определить, кто вы (неверный пароль, плохой токен).</summary>
public sealed class UnauthorizedException : AppException
{
    public UnauthorizedException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>HTTP 403: вы известны, но действие вам недоступно.</summary>
public sealed class ForbiddenException : AppException
{
    public ForbiddenException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>HTTP 429: слишком много попыток (например, аккаунт временно заблокирован после неверных паролей).</summary>
public sealed class TooManyRequestsException : AppException
{
    public TooManyRequestsException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>HTTP 404: такого ресурса нет (или он чужой: отличать это от «нет» нельзя).</summary>
public sealed class NotFoundException : AppException
{
    public NotFoundException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>HTTP 503: нужная внешняя проверка сейчас невозможна (например, не отвечает Cloudflare). Запрос не выполняется.</summary>
public sealed class ServiceUnavailableException : AppException
{
    public ServiceUnavailableException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>HTTP 413: тело запроса больше допустимого.</summary>
public sealed class PayloadTooLargeException : AppException
{
    public PayloadTooLargeException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>HTTP 409: конфликт с текущим состоянием (например, email уже занят).</summary>
public sealed class ConflictException : AppException
{
    public ConflictException(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null)
        : base(code, message, extensions)
    {
    }
}
