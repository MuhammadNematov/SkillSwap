namespace SkillSwap.Domain.Exceptions;

/// <summary>
/// Недопустимый переход статуса (раздел 9 ТЗ). Превращается в HTTP 409 Conflict.
/// </summary>
public sealed class InvalidStateTransitionException : DomainException
{
    public const string ErrorCode = "invalid_state_transition";

    public InvalidStateTransitionException(string entity, string from, string to)
        : base(ErrorCode, $"{entity}: переход из '{from}' в '{to}' недопустим.")
    {
        Entity = entity;
        From = from;
        To = to;
    }

    public string Entity { get; }
    public string From { get; }
    public string To { get; }
}
