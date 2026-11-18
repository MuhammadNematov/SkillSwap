using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class UserBlockConfiguration : IEntityTypeConfiguration<UserBlock>
{
    public void Configure(EntityTypeBuilder<UserBlock> builder)
    {
        builder.ToTable("UserBlocks", t => t.HasCheckConstraint("CK_UserBlocks_NotSelf", "\"UserId\" <> \"BlockedUserId\""));

        builder.HasKey(b => new { b.UserId, b.BlockedUserId });

        // «Кто заблокировал меня»: подбор и профили проверяют блокировку в обе стороны.
        builder.HasIndex(b => b.BlockedUserId);

        // Удаление любого из двух пользователей убирает блокировку.
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.BlockedUserId).OnDelete(DeleteBehavior.Cascade);
    }
}
