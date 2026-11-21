using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class PersonalLinkRequestConfiguration : IEntityTypeConfiguration<PersonalLinkRequest>
{
    public void Configure(EntityTypeBuilder<PersonalLinkRequest> builder)
    {
        builder.ToTable("PersonalLinkRequests");

        builder.Property(r => r.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(r => r.TokenHash).IsUnique();

        // Запросы сотрудника (последний действующий, лимит запросов) и запросы к личному аккаунту.
        builder.HasIndex(r => new { r.StaffId, r.CreatedAt });
        builder.HasIndex(r => r.PersonalUserId);

        builder.HasOne<User>().WithMany().HasForeignKey(r => r.StaffId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.PersonalUserId).OnDelete(DeleteBehavior.Cascade);

        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}
