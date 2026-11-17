namespace SkillSwap.Domain.Exceptions;

/// <summary>
/// Базовое доменное исключение. <see cref="Code"/> — машинный код ошибки,
/// по нему фронтенд подбирает перевод (раздел 12 ТЗ).
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
