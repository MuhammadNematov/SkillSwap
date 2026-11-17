using SkillSwap.Application.Common;

namespace SkillSwap.Application.Tests.Common;

public class LessonLanguagesTests
{
    [Fact]
    public void Codes_are_unique_two_lowercase_letters_and_sorted()
    {
        var codes = LessonLanguages.All.Select(l => l.Code).ToList();

        Assert.All(codes, c => Assert.Matches("^[a-z]{2}$", c));
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Equal(codes.Order(StringComparer.Ordinal), codes);
        Assert.True(codes.Count >= 175);
    }

    [Fact]
    public void Every_language_has_a_name_in_all_three_languages_and_names_are_not_copy_paste_errors()
    {
        Assert.All(LessonLanguages.All, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.NameTg));
            Assert.False(string.IsNullOrWhiteSpace(l.NameRu));
            Assert.False(string.IsNullOrWhiteSpace(l.NameEn));
            Assert.Equal(l.NameEn, l.NameEn.Trim());
        });
        Assert.Equal(LessonLanguages.All.Count, LessonLanguages.All.Select(l => l.NameEn).Distinct().Count());
        Assert.Equal(LessonLanguages.All.Count, LessonLanguages.All.Select(l => l.NameRu).Distinct().Count());
    }

    [Theory]
    [InlineData("tg")]
    [InlineData("ru")]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("zh")]
    [InlineData("ar")]
    [InlineData("fa")]
    [InlineData("tr")]
    [InlineData("ja")]
    [InlineData("hi")]
    [InlineData("uz")]
    public void Common_languages_are_in_the_list(string code) => Assert.True(LessonLanguages.IsValid(code));

    [Theory]
    [InlineData("")]
    [InlineData("RU")]      // канонический вид — строчные; приведение делает Normalize
    [InlineData("rus")]
    [InlineData("xx")]
    [InlineData(null)]
    public void Other_codes_are_not(string? code) => Assert.False(LessonLanguages.IsValid(code));

    [Theory]
    [InlineData(" RU ", "ru")]
    [InlineData("Tg", "tg")]
    public void Normalize_trims_and_lowercases(string input, string expected) => Assert.Equal(expected, LessonLanguages.Normalize(input));
}
