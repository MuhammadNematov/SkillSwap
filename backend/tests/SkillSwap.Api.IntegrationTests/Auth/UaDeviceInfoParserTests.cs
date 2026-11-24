using SkillSwap.Application.Auth;
using SkillSwap.Infrastructure.Auth;

namespace SkillSwap.Api.IntegrationTests.Auth;

/// <summary>Название устройства в списке сеансов: «Браузер · система», без места, провайдера и модели. Настоящие строки User-Agent.</summary>
public class UaDeviceInfoParserTests
{
    private static readonly UaDeviceInfoParser Parser = new();

    [Theory]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Safari/605.1.15", "Safari · macOS", DeviceType.Desktop)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36", "Chrome · macOS", DeviceType.Desktop)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36", "Chrome · Windows", DeviceType.Desktop)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0", "Edge · Windows", DeviceType.Desktop)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:125.0) Gecko/20100101 Firefox/125.0", "Firefox · Windows", DeviceType.Desktop)]
    [InlineData("Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:125.0) Gecko/20100101 Firefox/125.0", "Firefox · Linux", DeviceType.Desktop)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Mobile Safari/537.36", "Chrome · Android", DeviceType.Phone)]
    [InlineData("Mozilla/5.0 (Linux; Android 13; SM-X700) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36", "Chrome · Android", DeviceType.Tablet)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1", "Safari · iPhone", DeviceType.Phone)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/124.0.6367.88 Mobile/15E148 Safari/604.1", "Chrome · iPhone", DeviceType.Phone)]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1", "Safari · iPad", DeviceType.Tablet)]
    public void Browsers_are_named_by_browser_and_system(string userAgent, string name, DeviceType type)
    {
        var device = Parser.Describe(userAgent);

        Assert.Equal(name, device.Name);
        Assert.Equal(type, device.Type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("curl/8.7.1")]                                                              // не браузер
    [InlineData("SkillSwap-Test/1.0")]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]   // поисковый робот
    [InlineData("\u0000\u0001 not a user agent �")]
    public void What_cannot_be_recognised_is_unknown_not_an_error(string? userAgent)
    {
        var device = Parser.Describe(userAgent);

        Assert.Null(device.Name);
        Assert.Equal(DeviceType.Unknown, device.Type);
    }

    [Fact]
    public void A_very_long_user_agent_does_not_break_the_parser()
    {
        var device = Parser.Describe("Mozilla/5.0 (Windows NT 10.0; Win64; x64) " + new string('x', 5000) + " Chrome/124.0.0.0 Safari/537.36");

        Assert.NotNull(device);   // главное: не падает и не виснет
    }

    [Fact]
    public void The_name_never_contains_the_model_or_the_version()
    {
        var device = Parser.Describe("Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.6367.82 Mobile Safari/537.36");

        Assert.Equal("Chrome · Android", device.Name);
        Assert.DoesNotContain("SM-S918B", device.Name);
        Assert.DoesNotContain("124", device.Name);
    }
}
