namespace SkillSwap.Application.Exceptions;

/// <summary>
/// Ожидаемая ошибка сервиса. Code — машинный код для перевода на фронтенде.
/// Глобальный обработчик превращает её в ProblemDetails.
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>HTTP 400: запрос некорректен.</summary>
public sealed class BadRequestException : AppException
{
    public BadRequestException(string code, string message) : base(code, message)
    {
    }
}

/// <summary>HTTP 409: конфликт с текущим состоянием (например, email уже занят).</summary>
public sealed class ConflictException : AppException
{
    public ConflictException(string code, string message) : base(code, message)
    {
    }
}
