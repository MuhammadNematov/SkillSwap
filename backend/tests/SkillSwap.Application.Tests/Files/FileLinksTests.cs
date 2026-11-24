using SkillSwap.Application.Files;
using SkillSwap.Application.Options;

namespace SkillSwap.Application.Tests.Files;

/// <summary>Ссылки на файлы в письмах: относительный путь (режим Storage:RelativeUrls) дополняется адресом сайта, абсолютная ссылка не меняется.</summary>
public class FileLinksTests
{
    private static FrontendOptions Frontend(string baseUrl) => new() { BaseUrl = baseUrl };

    [Theory]
    [InlineData("https://app.example", "/skillswap/exports/a/b?X-Amz-Signature=1", "https://app.example/skillswap/exports/a/b?X-Amz-Signature=1")]
    [InlineData("https://app.example/", "/skillswap/exports/a/b?X-Amz-Signature=1", "https://app.example/skillswap/exports/a/b?X-Amz-Signature=1")]   // косая черта в конце адреса не удваивается
    [InlineData("http://localhost:5173", "/skillswap/x", "http://localhost:5173/skillswap/x")]
    public void A_relative_link_gets_the_site_address_in_front(string baseUrl, string link, string expected) =>
        Assert.Equal(expected, FileLinks.Absolute(link, Frontend(baseUrl)));

    [Theory]
    [InlineData("https://files.example.com/skillswap/exports/a/b?X-Amz-Signature=1")]
    [InlineData("http://localhost:3900/skillswap/x")]
    [InlineData("//cdn.example.com/x")]                 // «адрес без схемы» не наш случай: не трогаем
    public void An_absolute_link_stays_as_it_is(string link) =>
        Assert.Equal(link, FileLinks.Absolute(link, Frontend("https://app.example")));
}
