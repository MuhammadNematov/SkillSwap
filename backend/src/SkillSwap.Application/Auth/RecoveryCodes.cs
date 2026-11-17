using System.Security.Cryptography;
using System.Text;
using SkillSwap.Application.Common;

namespace SkillSwap.Application.Auth;

/// <summary>
/// Резервные коды 2FA. Код — 10 случайных байт (80 бит) в base32, показывается как «XXXX-XXXX-XXXX-XXXX».
/// Случайности достаточно, чтобы в БД хранить обычный SHA-256 (как для refresh-токенов), без соли и медленного хеша.
/// </summary>
public static class RecoveryCodes
{
    private const int GroupSize = 4;

    /// <summary>Создаёт <paramref name="count"/> уникальных кодов в человекочитаемом виде.</summary>
    public static IReadOnlyList<string> Generate(int count)
    {
        var codes = new HashSet<string>(count);
        while (codes.Count < count)
        {
            var raw = Base32.Encode(RandomNumberGenerator.GetBytes(10));   // 16 символов
            codes.Add(string.Join('-', Enumerable.Range(0, raw.Length / GroupSize).Select(i => raw.Substring(i * GroupSize, GroupSize))));
        }

        return codes.ToList();
    }

    /// <summary>
    /// Приводит ввод пользователя к каноническому виду: без пробелов и дефисов, в верхнем регистре.
    /// Так «abcd-efgh ijkl-mnop» и «ABCDEFGHIJKLMNOP» — один и тот же код.
    /// </summary>
    public static string Normalize(string code) =>
        new(code.Where(c => c is not (' ' or '-' or '\t')).Select(char.ToUpperInvariant).ToArray());

    /// <summary>SHA-256 канонического вида в hex (верхний регистр). Именно это лежит в БД.</summary>
    public static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));
}
