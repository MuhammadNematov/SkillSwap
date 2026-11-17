using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence.Seed;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class SkillCategoryConfiguration : IEntityTypeConfiguration<SkillCategory>
{
    public void Configure(EntityTypeBuilder<SkillCategory> builder)
    {
        builder.ToTable("SkillCategories");

        builder.Property(c => c.NameTg).HasMaxLength(SkillCategory.NameMaxLength).IsRequired();
        builder.Property(c => c.NameRu).HasMaxLength(SkillCategory.NameMaxLength).IsRequired();
        builder.Property(c => c.NameEn).HasMaxLength(SkillCategory.NameMaxLength).IsRequired();

        // Две категории с одним английским названием не нужны; регистр сравнивает сервис, индекс — запасная защита.
        builder.HasIndex(c => c.NameEn).IsUnique();
        builder.HasIndex(c => c.SortOrder);

        builder.HasData(SkillCatalogSeed.Categories);
    }
}

public class SkillConfiguration : IEntityTypeConfiguration<Skill>
{
    public void Configure(EntityTypeBuilder<Skill> builder)
    {
        builder.ToTable("Skills");

        builder.Property(s => s.NameTg).HasMaxLength(Skill.NameMaxLength).IsRequired();
        builder.Property(s => s.NameRu).HasMaxLength(Skill.NameMaxLength).IsRequired();
        builder.Property(s => s.NameEn).HasMaxLength(Skill.NameMaxLength).IsRequired();
        builder.Property(s => s.Synonyms).HasColumnType("text[]").IsRequired();

        // Навык нельзя удалить, пока на него ссылаются анкеты; его выключают (IsActive).
        builder.HasOne(s => s.Category).WithMany(c => c.Skills)
            .HasForeignKey(s => s.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.CategoryId, s.NameEn }).IsUnique();
        builder.HasIndex(s => s.Synonyms).HasMethod("gin");   // раздел 13 ТЗ

        builder.HasData(SkillCatalogSeed.Skills);
    }
}
