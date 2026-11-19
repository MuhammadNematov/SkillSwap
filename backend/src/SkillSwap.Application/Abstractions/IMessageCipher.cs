namespace SkillSwap.Application.Abstractions;

/// <summary>Шифрование текста сообщений чата на сервере (Data Protection API, раздел 10.1 ТЗ).</summary>
public interface IMessageCipher
{
    string Encrypt(string plainText);

    /// <summary>Расшифровывает; если данные повреждены или ключа нет, бросает исключение (молча подменять текст нельзя).</summary>
    string Decrypt(string cipherText);
}
