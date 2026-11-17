using SkillSwap.Application.Common;

namespace SkillSwap.Application.Tests.Common;

public class SupportedLanguagesTests
{
    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("ru", "ru")]
    [InlineData("ru-RU,ru;q=0.9,en;q=0.8", "ru")]
    [InlineData("de-DE,de;q=0.9,tg;q=0.7,en;q=0.5", "tg")]   // de не поддерживаем, берём лучший из поддерживаемых
    [InlineData("en;q=0.5,ru;q=0.9", "ru")]                   // порядок по весу, а не по позиции
    [InlineData("fr,de", "en")]                               // ничего не подошло
    [InlineData("ru;q=0", "en")]                              // q=0 — язык запрещён
    [InlineData("*", "en")]
    public void FromAcceptLanguage_picks_best_supported(string? header, string expected)
    {
        Assert.Equal(expected, SupportedLanguages.FromAcceptLanguage(header));
    }
}
