using System.Security.Cryptography;
using System.Text;

namespace SkillSwap.Application.Auth;

/// <summary>Шестизначный код подтверждения email: выпуск, хеш и проверка. Код живёт 15 минут, на него даётся 5 попыток.</summary>
public static class EmailConfirmationCodes
{
    public const int Length = 6;
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Равномерно случайные цифры; ведущие нули сохраняются («004217»).</summary>
    public static string Generate() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>Приводит введённое к виду кода: пробелы и дефисы (копирование «123 456») убираются. null — не похоже на код.</summary>
    public static string? Normalize(string? input)
    {
        var digits = new string((input ?? string.Empty).Where(c => c is not (' ' or '-' or ' ')).ToArray());
        return digits.Length == Length && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    /// <summary>Хеш привязан к пользователю: одинаковый код у двух людей даёт разные хеши. Код короткий и живёт недолго, поэтому достаточно SHA-256.</summary>
    public static string Hash(Guid userId, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:N}:{code}")));

    public static bool Matches(Guid userId, string code, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(userId, code)), Encoding.ASCII.GetBytes(storedHash));
}
