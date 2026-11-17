using SkillSwap.Application.Auth;

namespace SkillSwap.Application.Tests.Auth;

public class RecoveryCodesTests
{
    [Fact]
    public void Generate_makes_unique_codes_with_80_bits_in_a_readable_format()
    {
        var codes = RecoveryCodes.Generate(10);

        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){3}$", c));   // 16 символов base32 = 80 бит
    }

    [Fact]
    public void Generate_does_not_repeat_codes_between_calls()
    {
        var all = Enumerable.Range(0, 50).SelectMany(_ => RecoveryCodes.Generate(10)).ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Theory]
    [InlineData("ABCD-EFGH-IJKL-MNOP", "ABCDEFGHIJKLMNOP")]
    [InlineData("abcd-efgh-ijkl-mnop", "ABCDEFGHIJKLMNOP")]
    [InlineData("  abcd efgh ijkl mnop ", "ABCDEFGHIJKLMNOP")]
    [InlineData("ABCDEFGHIJKLMNOP", "ABCDEFGHIJKLMNOP")]
    [InlineData("AB-CD\tEF", "ABCDEF")]
    public void Normalize_ignores_case_spaces_and_dashes(string input, string expected)
    {
        Assert.Equal(expected, RecoveryCodes.Normalize(input));
    }

    [Fact]
    public void Hash_is_the_same_for_every_way_of_typing_the_code()
    {
        var hash = RecoveryCodes.Hash("ABCD-EFGH-IJKL-MNOP");

        Assert.Equal(hash, RecoveryCodes.Hash("abcdefghijklmnop"));
        Assert.Equal(hash, RecoveryCodes.Hash(" abcd efgh ijkl mnop "));
        Assert.NotEqual(hash, RecoveryCodes.Hash("ABCD-EFGH-IJKL-MNOQ"));
    }

    [Fact]
    public void Hash_is_an_uppercase_hex_SHA256_that_does_not_contain_the_code()
    {
        var hash = RecoveryCodes.Hash("ABCD-EFGH-IJKL-MNOP");

        Assert.Matches("^[0-9A-F]{64}$", hash);
        Assert.DoesNotContain("ABCD", hash.Replace("-", string.Empty), StringComparison.Ordinal);
    }
}
