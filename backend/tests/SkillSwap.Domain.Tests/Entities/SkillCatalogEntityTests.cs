using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

public class SkillCatalogEntityTests
{
    private static readonly Guid CategoryId = Guid.NewGuid();

    [Fact]
    public void A_category_trims_its_names()
    {
        var category = SkillCategory.Create("  Дизайн ", " Дизайн", "Design  ", 3);

        Assert.Equal("Дизайн", category.NameTg);
        Assert.Equal("Design", category.NameEn);
        Assert.True(category.IsActive);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_name_cannot_be_empty(string? name)
    {
        var ex = Assert.Throws<DomainValidationException>(() => SkillCategory.Create("a", "b", name!, 1));
        Assert.Equal("name_required", ex.Code);
        Assert.Throws<DomainValidationException>(() => Skill.Create(CategoryId, name!, "b", "c"));
    }

    [Fact]
    public void A_name_has_a_length_limit()
    {
        var ok = new string('a', Skill.NameMaxLength);
        Skill.Create(CategoryId, ok, ok, ok);

        var ex = Assert.Throws<DomainValidationException>(() => Skill.Create(CategoryId, ok + "a", ok, ok));
        Assert.Equal("name_too_long", ex.Code);
    }

    [Fact]
    public void Synonyms_are_trimmed_lowercased_and_deduplicated()
    {
        var skill = Skill.Create(CategoryId, "JS", "JS", "JS", [" JS ", "js", "Джаваскрипт", "", "   "]);

        Assert.Equal(["js", "джаваскрипт"], skill.Synonyms);
    }

    [Fact]
    public void Synonyms_are_limited_in_number_and_length()
    {
        var many = Enumerable.Range(0, Skill.MaxSynonyms + 1).Select(i => "s" + i);
        Assert.Equal("too_many_synonyms", Assert.Throws<DomainValidationException>(() => Skill.Create(CategoryId, "a", "b", "c", many)).Code);

        var longOne = new string('x', Skill.SynonymMaxLength + 1);
        Assert.Equal("synonym_too_long", Assert.Throws<DomainValidationException>(() => Skill.Create(CategoryId, "a", "b", "c", [longOne])).Code);

        Skill.Create(CategoryId, "a", "b", "c", Enumerable.Range(0, Skill.MaxSynonyms).Select(i => "s" + i));   // ровно предел — можно
    }

    [Fact]
    public void Update_replaces_everything_and_can_switch_a_skill_off()
    {
        var skill = Skill.Create(CategoryId, "a", "b", "c", ["x"]);
        var other = Guid.NewGuid();

        skill.Update(other, "A", "B", "C", ["y"], isActive: false);

        Assert.Equal(other, skill.CategoryId);
        Assert.Equal("C", skill.NameEn);
        Assert.Equal(["y"], skill.Synonyms);
        Assert.False(skill.IsActive);
    }

    [Fact]
    public void A_failed_update_does_not_leave_a_half_changed_skill()
    {
        var skill = Skill.Create(CategoryId, "a", "b", "c", ["x"]);

        Assert.Throws<DomainValidationException>(() => skill.Update(Guid.NewGuid(), "new", "new", "", ["z"], false));

        Assert.Equal(CategoryId, skill.CategoryId);
        Assert.Equal("a", skill.NameTg);
        Assert.Equal("b", skill.NameRu);
        Assert.Equal(["x"], skill.Synonyms);
        Assert.True(skill.IsActive);

        var category = SkillCategory.Create("a", "b", "c", 1);
        Assert.Throws<DomainValidationException>(() => category.Update("new", "new", "", 5, false));
        Assert.Equal("a", category.NameTg);
        Assert.Equal(1, category.SortOrder);
        Assert.True(category.IsActive);
    }
}
