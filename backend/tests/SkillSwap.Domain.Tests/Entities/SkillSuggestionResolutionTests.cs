using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Решение админа по предложению навыка и синонимы навыка при объединении.</summary>
public class SkillSuggestionResolutionTests
{
    private static SkillSuggestion Pending() => SkillSuggestion.Create(Guid.NewGuid(), Guid.NewGuid(), "Rust");

    [Fact]
    public void Approving_and_merging_remember_the_skill_and_the_admin()
    {
        var admin = Guid.NewGuid();
        var skill = Guid.NewGuid();
        var approved = Pending();
        var merged = Pending();

        approved.Approve(admin, skill);
        merged.Merge(admin, skill);

        Assert.Equal(SuggestionStatus.Approved, approved.Status);
        Assert.Equal(SuggestionStatus.Merged, merged.Status);
        Assert.All(new[] { approved, merged }, s =>
        {
            Assert.Equal(skill, s.ResolvedSkillId);
            Assert.Equal(admin, s.ResolvedById);
        });
    }

    [Fact]
    public void Rejecting_leaves_no_skill()
    {
        var s = Pending();

        s.Reject(Guid.NewGuid());

        Assert.Equal(SuggestionStatus.Rejected, s.Status);
        Assert.Null(s.ResolvedSkillId);
        Assert.NotNull(s.ResolvedById);
    }

    [Fact]
    public void A_resolved_suggestion_cannot_be_resolved_again()
    {
        var s = Pending();
        s.Reject(Guid.NewGuid());

        Assert.Throws<InvalidStateTransitionException>(() => s.Approve(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<InvalidStateTransitionException>(() => s.Merge(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<InvalidStateTransitionException>(() => s.Reject(Guid.NewGuid()));
    }

    [Fact]
    public void A_synonym_is_added_in_lower_case_once_and_never_duplicates_a_name_or_exceeds_the_limit()
    {
        var skill = Skill.Create(Guid.NewGuid(), "Ҷаваскрипт", "Джаваскрипт", "JavaScript", ["js"]);

        Assert.True(skill.AddSynonym("  ДЖС "));
        Assert.False(skill.AddSynonym("джс"));          // уже есть
        Assert.False(skill.AddSynonym("JS"));           // уже есть без учёта регистра
        Assert.False(skill.AddSynonym("javascript"));   // совпало с названием
        Assert.False(skill.AddSynonym("   "));
        Assert.Equal(["js", "джс"], skill.Synonyms);

        for (var i = 0; skill.Synonyms.Length < Skill.MaxSynonyms; i++)
        {
            Assert.True(skill.AddSynonym("s" + i));
        }

        Assert.False(skill.AddSynonym("one more"));
        Assert.Equal(Skill.MaxSynonyms, skill.Synonyms.Length);
    }
}
