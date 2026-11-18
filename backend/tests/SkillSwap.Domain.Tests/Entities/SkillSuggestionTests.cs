using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

public class SkillSuggestionTests
{
    [Theory]
    [InlineData("  Rust  ", "Rust")]
    [InlineData("Rust   and   Go", "Rust and Go")]
    [InlineData("Тоҷикӣ\tзабон", "Тоҷикӣ забон")]   // табуляция между словами — это пробел, а не «управляющий символ в названии»
    [InlineData("C#", "C#")]
    [InlineData("Go", "Go")]
    public void CleanName_collapses_whitespace(string input, string expected) =>
        Assert.Equal(expected, SkillSuggestion.CleanName(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("1234")]
    [InlineData("?!")]
    [InlineData("ab\u0000cd")]
    public void CleanName_rejects_unusable_names(string? input) =>
        Assert.Equal("invalid_name", Assert.Throws<DomainValidationException>(() => SkillSuggestion.CleanName(input)).Code);

    [Fact]
    public void CleanName_limits_the_length_to_one_hundred()
    {
        Assert.Equal(100, SkillSuggestion.CleanName(new string('я', 100)).Length);
        Assert.Throws<DomainValidationException>(() => SkillSuggestion.CleanName(new string('я', 101)));
    }

    [Fact]
    public void A_new_suggestion_is_pending_with_a_lowercase_comparison_name()
    {
        var userId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();

        var suggestion = SkillSuggestion.Create(userId, categoryId, "  Machine   Learning ");

        Assert.Equal("Machine Learning", suggestion.SuggestedName);
        Assert.Equal("machine learning", suggestion.NormalizedName);
        Assert.Equal(userId, suggestion.UserId);
        Assert.Equal(categoryId, suggestion.CategoryId);
        Assert.Equal(SuggestionStatus.Pending, suggestion.Status);
        Assert.Null(suggestion.ResolvedSkillId);
        Assert.Null(suggestion.ResolvedById);
    }
}
