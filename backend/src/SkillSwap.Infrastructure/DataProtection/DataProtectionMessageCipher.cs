using Microsoft.AspNetCore.DataProtection;
using SkillSwap.Application.Abstractions;

namespace SkillSwap.Infrastructure.DataProtection;

/// <summary>
/// Шифрует сообщения через Data Protection (ключи лежат в БД и защищены сертификатом вне БД, раздел 10.1 ТЗ).
/// Назначение (purpose) отделяет эти данные от остальных: токены и cookie другого назначения расшифровать ими нельзя.
/// </summary>
public sealed class DataProtectionMessageCipher : IMessageCipher
{
    public const string Purpose = "SkillSwap.Chat.Messages.v1";

    private readonly IDataProtector _protector;

    public DataProtectionMessageCipher(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Encrypt(string plainText) => _protector.Protect(plainText);

    public string Decrypt(string cipherText) => _protector.Unprotect(cipherText);
}
