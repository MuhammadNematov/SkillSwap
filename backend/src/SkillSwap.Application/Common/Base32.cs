namespace SkillSwap.Application.Common;

/// <summary>Base32 по RFC 4648 (алфавит A–Z, 2–7), без заполнения «=». Так хранится ключ аутентификатора.</summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var result = new char[(data.Length * 8 + 4) / 5];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result[index++] = Alphabet[(buffer >> (bits - 5)) & 31];
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            result[index] = Alphabet[(buffer << (5 - bits)) & 31];
        }

        return new string(result);
    }

    /// <summary>Возвращает null, если в строке есть символы вне алфавита.</summary>
    public static byte[]? Decode(string input)
    {
        var bytes = new List<byte>(input.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return bytes.ToArray();
    }
}
