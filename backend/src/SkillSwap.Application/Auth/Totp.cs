using System.Globalization;
using System.Security.Cryptography;
using SkillSwap.Application.Common;

namespace SkillSwap.Application.Auth;

/// <summary>
/// TOTP по RFC 6238 (HMAC-SHA1, шаг 30 секунд, 6 цифр) — то, что считают приложения-аутентификаторы.
/// Проверку делаем сами, а не через Identity, потому что нужен номер совпавшего шага:
/// по нему запрещается повторное использование кода.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    /// <summary>Допуск на расхождение часов: принимаем предыдущий, текущий и следующий шаги.</summary>
    public const int Window = 1;

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>Код для указанного шага. Ключ — в base32, как его показывает аутентификатор.</summary>
    public static string Compute(string base32Key, long step)
    {
        var key = Base32.Decode(base32Key) ?? throw new ArgumentException("Ключ не в формате base32.", nameof(base32Key));
        return Compute(key, step);
    }

    internal static string Compute(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Ищет шаг, которому соответствует код, среди [сейчас − Window; сейчас + Window].
    /// Возвращает номер шага или null. Сравнение за постоянное время.
    /// </summary>
    public static long? FindMatchingStep(string base32Key, string code, DateTimeOffset now)
    {
        var key = Base32.Decode(base32Key);
        if (key is null || code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var codeBytes = System.Text.Encoding.ASCII.GetBytes(code);
        var current = StepAt(now);
        long? match = null;
        for (var step = current - Window; step <= current + Window; step++)
        {
            var expected = System.Text.Encoding.ASCII.GetBytes(Compute(key, step));
            // Проверяем все шаги без раннего выхода, чтобы время ответа не выдавало, какой из них подошёл.
            if (CryptographicOperations.FixedTimeEquals(expected, codeBytes))
            {
                match = step;
            }
        }

        return match;
    }
}
