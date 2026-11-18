using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class SkillSuggestionConfiguration : IEntityTypeConfiguration<SkillSuggestion>
{
    public void Configure(EntityTypeBuilder<SkillSuggestion> builder)
    {
        builder.ToTable("SkillSuggestions");

        builder.Property(s => s.SuggestedName).HasMaxLength(SkillSuggestion.NameMaxLength).IsRequired();
        builder.Property(s => s.NormalizedName).HasMaxLength(SkillSuggestion.NameMaxLength).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        // Один и тот же человек не может держать два неразобранных предложения с одним названием в одной категории
        // (в том числе при двух одновременных запросах: сработает этот индекс).
        builder.HasIndex(s => new { s.UserId, s.CategoryId, s.NormalizedName })
            .IsUnique()
            .HasFilter("\"Status\" = 'Pending'");

        builder.HasIndex(s => new { s.UserId, s.Status });
        builder.HasIndex(s => new { s.Status, s.CreatedAt });   // очередь админа (этап 7)

        builder.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<SkillCategory>().WithMany().HasForeignKey(s => s.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Skill>().WithMany().HasForeignKey(s => s.ResolvedSkillId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.ResolvedById).OnDelete(DeleteBehavior.SetNull);
    }
}
