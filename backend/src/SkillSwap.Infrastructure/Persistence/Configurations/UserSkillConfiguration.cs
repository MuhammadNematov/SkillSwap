using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class UserSkillConfiguration : IEntityTypeConfiguration<UserSkill>
{
    public void Configure(EntityTypeBuilder<UserSkill> builder)
    {
        builder.ToTable("UserSkills");

        builder.Property(s => s.Direction).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.Level).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(UserSkill.DescriptionMaxLength);

        // Раздел 5.2 и 13 ТЗ: один навык — один раз в каждом направлении.
        builder.HasIndex(s => new { s.UserId, s.SkillId, s.Direction }).IsUnique();

        // Подбор ищет «кто преподаёт навык X» и «кто хочет изучать навык X».
        builder.HasIndex(s => new { s.SkillId, s.Direction, s.UserId });

        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);

        // Навык нельзя удалить из каталога, пока он есть в чьей-то анкете: его выключают.
        builder.HasOne(s => s.Skill).WithMany().HasForeignKey(s => s.SkillId).OnDelete(DeleteBehavior.Restrict);
    }
}
