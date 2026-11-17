using System.Text;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Common;

namespace SkillSwap.Application.Tests.Auth;

public class TotpTests
{
    // Ключ из приложения B RFC 6238: ASCII «12345678901234567890» в base32.
    private const string RfcKey = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    // Контрольные значения RFC 6238 (SHA-1). В RFC они 8-значные, у нас 6 цифр — берутся последние шесть.
    [Theory]
    [InlineData(59L, "287082")]            // 94287082
    [InlineData(1111111109L, "081804")]    // 07081804
    [InlineData(1111111111L, "050471")]    // 14050471
    [InlineData(1234567890L, "005924")]    // 89005924
    [InlineData(2000000000L, "279037")]    // 69279037
    [InlineData(20000000000L, "353130")]   // 65353130
    public void Compute_matches_the_RFC_6238_test_vectors(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        Assert.Equal(expected, Totp.Compute(RfcKey, step));
    }

    [Fact]
    public void StepAt_changes_every_30_seconds()
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);   // кратно 30
        Assert.Equal(Totp.StepAt(t), Totp.StepAt(t.AddSeconds(29)));
        Assert.Equal(Totp.StepAt(t) + 1, Totp.StepAt(t.AddSeconds(30)));
    }

    [Fact]
    public void FindMatchingStep_returns_the_step_of_the_matching_code()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_015);
        var current = Totp.StepAt(now);

        Assert.Equal(current - 1, Totp.FindMatchingStep(RfcKey, Totp.Compute(RfcKey, current - 1), now));
        Assert.Equal(current, Totp.FindMatchingStep(RfcKey, Totp.Compute(RfcKey, current), now));
        Assert.Equal(current + 1, Totp.FindMatchingStep(RfcKey, Totp.Compute(RfcKey, current + 1), now));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    [InlineData(100)]
    public void FindMatchingStep_rejects_codes_outside_the_window(int offset)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_015);

        var code = Totp.Compute(RfcKey, Totp.StepAt(now) + offset);

        Assert.Null(Totp.FindMatchingStep(RfcKey, code, now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("١٢٣٤٥٦")]   // арабско-индийские цифры: IsDigit их пропустил бы, IsAsciiDigit — нет
    public void FindMatchingStep_rejects_malformed_codes(string code)
    {
        Assert.Null(Totp.FindMatchingStep(RfcKey, code, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void FindMatchingStep_rejects_a_key_that_is_not_base32()
    {
        Assert.Null(Totp.FindMatchingStep("not base32!", "123456", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Codes_are_zero_padded_to_six_digits()
    {
        var codes = Enumerable.Range(0, 2000).Select(i => Totp.Compute(RfcKey, i)).ToList();

        Assert.All(codes, c => Assert.Equal(6, c.Length));
        Assert.Contains(codes, c => c.StartsWith('0'));   // и ведущие нули сохраняются
    }

    // ---------- base32 ----------

    [Fact]
    public void Base32_matches_the_RFC_4648_vectors()
    {
        Assert.Equal("", Base32.Encode(Encoding.ASCII.GetBytes("")));
        Assert.Equal("MY", Base32.Encode(Encoding.ASCII.GetBytes("f")));
        Assert.Equal("MZXQ", Base32.Encode(Encoding.ASCII.GetBytes("fo")));
        Assert.Equal("MZXW6", Base32.Encode(Encoding.ASCII.GetBytes("foo")));
        Assert.Equal("MZXW6YQ", Base32.Encode(Encoding.ASCII.GetBytes("foob")));
        Assert.Equal("MZXW6YTB", Base32.Encode(Encoding.ASCII.GetBytes("fooba")));
        Assert.Equal("MZXW6YTBOI", Base32.Encode(Encoding.ASCII.GetBytes("foobar")));
    }

    [Theory]
    [InlineData("MY", "f")]
    [InlineData("MZXW6YTBOI", "foobar")]
    [InlineData("mzxw6ytboi", "foobar")]          // регистр не важен
    [InlineData("MZXW6YTBOI======", "foobar")]    // заполнение допускается
    public void Base32_decodes(string input, string expected)
    {
        Assert.Equal(expected, Encoding.ASCII.GetString(Base32.Decode(input)!));
    }

    [Fact]
    public void Base32_round_trips_random_data_and_rejects_foreign_characters()
    {
        var data = System.Security.Cryptography.RandomNumberGenerator.GetBytes(20);

        Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
        Assert.Null(Base32.Decode("MZXW6YTB0I"));   // «0» нет в алфавите
    }
}
