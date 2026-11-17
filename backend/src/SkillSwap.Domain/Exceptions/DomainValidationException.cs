namespace SkillSwap.Domain.Exceptions;

/// <summary>
/// Данные нарушают правило предметной области (пустое или слишком длинное название и т. п.). Превращается в HTTP 400.
/// Сервисы проверяют вход раньше; это последний рубеж, чтобы в БД не попало неправильное.
/// </summary>
public sealed class DomainValidationException : DomainException
{
    public DomainValidationException(string code, string message) : base(code, message)
    {
    }
}
