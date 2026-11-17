using System.Security.Cryptography;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Генератор TOTP по RFC 6238 (HMAC-SHA1, 30 секунд, 6 цифр) — то, что делает приложение-аутентификатор.
/// Написан независимо от сервера, поэтому тесты проверяют совместимость с настоящими аутентификаторами.
/// </summary>
public static class Totp
{
    public static string Now(string base32Key, DateTimeOffset? at = null, int stepOffset = 0)
    {
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30 + stepOffset;
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        var hash = HMACSHA1.HashData(DecodeBase32(base32Key), counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] DecodeBase32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value = 0;
        var result = new List<byte>();
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)(value >> (bits - 8)));
                bits -= 8;
            }
        }

        return result.ToArray();
    }
}
