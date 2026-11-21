using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class StaffInvitationConfiguration : IEntityTypeConfiguration<StaffInvitation>
{
    public void Configure(EntityTypeBuilder<StaffInvitation> builder)
    {
        builder.ToTable("StaffInvitations");

        builder.Property(i => i.Email).HasMaxLength(StaffInvitation.EmailMaxLength).IsRequired();
        builder.Property(i => i.TargetRole).HasConversion<string>().HasMaxLength(16);
        builder.Property(i => i.TokenHash).HasMaxLength(64).IsRequired();

        // Токен из ссылки находят по хешу; два приглашения с одним хешем быть не могут.
        builder.HasIndex(i => i.TokenHash).IsUnique();

        // Повторные приглашения на один адрес и список «кого и когда пригласили».
        builder.HasIndex(i => i.Email);
        builder.HasIndex(i => new { i.InvitedById, i.CreatedAt });

        builder.HasOne<User>().WithMany().HasForeignKey(i => i.InvitedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(i => i.AcceptedUserId).OnDelete(DeleteBehavior.Restrict);

        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}
