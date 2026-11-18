using System.Security.Cryptography;
using SkillSwap.Application.Auth;

namespace SkillSwap.Application.Tests.Auth;

public class UrlSafeTokenTests
{
    [Fact]
    public void A_standard_token_becomes_url_safe_and_back_exactly()
    {
        // Все длины от 1 до 200 байт: покрывают отступы «=», «==» и без них; случайные байты дают и «+», и «/».
        for (var length = 1; length <= 200; length++)
        {
            var standard = Convert.ToBase64String(RandomNumberGenerator.GetBytes(length));

            var safe = UrlSafeToken.Encode(standard);

            Assert.Matches("^[A-Za-z0-9_-]*$", safe);
            Assert.Equal(standard, UrlSafeToken.Decode(safe));
        }
    }

    [Fact]
    public void The_url_safe_form_is_what_dotnet_calls_base64url()
    {
        var bytes = new byte[] { 0xFB, 0xFF, 0xBE, 0x3E, 0x3F };

        Assert.Equal(System.Buffers.Text.Base64Url.EncodeToString(bytes), UrlSafeToken.Encode(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void A_standard_token_passes_through_decoding_unchanged()
    {
        var standard = Convert.ToBase64String(RandomNumberGenerator.GetBytes(77));

        Assert.Equal(standard, UrlSafeToken.Decode(standard));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("%%%", "%%%=")]
    [InlineData("a b", "a b=")]    // мусор остаётся мусором; главное — без исключений
    [InlineData("-", "+===")]
    public void Decoding_never_throws(string? input, string expected) => Assert.Equal(expected, UrlSafeToken.Decode(input));
}
