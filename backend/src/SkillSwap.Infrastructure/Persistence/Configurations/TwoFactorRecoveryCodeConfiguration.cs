using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class TwoFactorRecoveryCodeConfiguration : IEntityTypeConfiguration<TwoFactorRecoveryCode>
{
    public void Configure(EntityTypeBuilder<TwoFactorRecoveryCode> builder)
    {
        builder.ToTable("TwoFactorRecoveryCodes");

        builder.Property(c => c.CodeHash).HasMaxLength(64).IsRequired();

        builder.HasIndex(c => c.UserId);
        // Коды случайные (80 бит), поэтому хеш уникален во всей таблице.
        builder.HasIndex(c => c.CodeHash).IsUnique();

        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
