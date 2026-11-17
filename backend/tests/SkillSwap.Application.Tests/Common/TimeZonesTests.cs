using SkillSwap.Application.Common;

namespace SkillSwap.Application.Tests.Common;

public class TimeZonesTests
{
    [Theory]
    [InlineData("Asia/Dushanbe")]
    [InlineData("Europe/Moscow")]
    [InlineData("UTC")]
    [InlineData("Etc/GMT+5")]
    [InlineData("America/Argentina/Buenos_Aires")]
    public void Real_iana_identifiers_are_valid(string id) => Assert.True(TimeZones.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Mars/Phobos")]
    [InlineData("Tajikistan Standard Time")]   // название Windows, не IANA
    [InlineData("asia/dushanbe")]              // регистр важен: на разных системах ответ был бы разным
    [InlineData("Asia/DUSHANBE")]
    [InlineData("Asia/Dushanbe ")]
    [InlineData("../etc/passwd")]
    [InlineData("Asia/../Dushanbe")]
    [InlineData("Asia\\Dushanbe")]
    [InlineData("UTC+5")]
    [InlineData("Local")]
    public void Anything_else_is_invalid(string? id) => Assert.False(TimeZones.IsValid(id));

    [Fact]
    public void An_overlong_identifier_is_invalid()
    {
        Assert.False(TimeZones.IsValid("Asia/" + new string('a', TimeZones.MaxLength)));
    }
}
