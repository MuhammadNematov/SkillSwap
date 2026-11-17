using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

public class UserSkillTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid SkillId = Guid.NewGuid();

    [Fact]
    public void A_skill_in_the_anketa_keeps_direction_level_and_a_trimmed_description()
    {
        var skill = UserSkill.Create(UserId, SkillId, SkillDirection.Teach, SkillLevel.Advanced, "  5 лет  ");

        Assert.Equal(UserId, skill.UserId);
        Assert.Equal(SkillId, skill.SkillId);
        Assert.Equal(SkillDirection.Teach, skill.Direction);
        Assert.Equal(SkillLevel.Advanced, skill.Level);
        Assert.Equal("5 лет", skill.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_description_becomes_null(string? description)
    {
        Assert.Null(UserSkill.Create(UserId, SkillId, SkillDirection.Learn, SkillLevel.Beginner, description).Description);
    }

    [Fact]
    public void The_description_has_a_limit_and_a_rejected_update_changes_nothing()
    {
        var skill = UserSkill.Create(UserId, SkillId, SkillDirection.Teach, SkillLevel.Beginner, "было");

        var ex = Assert.Throws<DomainValidationException>(() => skill.Update(SkillLevel.Advanced, new string('a', UserSkill.DescriptionMaxLength + 1)));

        Assert.Equal("description_too_long", ex.Code);
        Assert.Equal(SkillLevel.Beginner, skill.Level);
        Assert.Equal("было", skill.Description);
        skill.Update(SkillLevel.Advanced, new string('a', UserSkill.DescriptionMaxLength));   // ровно предел — можно
    }

    [Fact]
    public void An_undefined_level_is_refused()
    {
        var ex = Assert.Throws<DomainValidationException>(() => UserSkill.Create(UserId, SkillId, SkillDirection.Teach, (SkillLevel)0, null));

        Assert.Equal("invalid_level", ex.Code);
        Assert.Throws<DomainValidationException>(() => UserSkill.Create(UserId, SkillId, SkillDirection.Teach, (SkillLevel)7, null));
    }

    [Fact]
    public void Levels_are_ordered_so_that_a_higher_one_is_a_bigger_number()
    {
        Assert.True(SkillLevel.Beginner < SkillLevel.Intermediate);
        Assert.True(SkillLevel.Intermediate < SkillLevel.Advanced);
    }
}
