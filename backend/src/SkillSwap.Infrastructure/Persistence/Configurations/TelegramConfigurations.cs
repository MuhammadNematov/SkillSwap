using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class TelegramLinkConfiguration : IEntityTypeConfiguration<TelegramLink>
{
    public void Configure(EntityTypeBuilder<TelegramLink> builder)
    {
        builder.ToTable("TelegramLinks");
        builder.HasIndex(l => l.UserId).IsUnique();   // у аккаунта один чат
        builder.HasIndex(l => l.ChatId).IsUnique();   // у чата один аккаунт
        builder.HasOne<User>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TelegramLinkCodeConfiguration : IEntityTypeConfiguration<TelegramLinkCode>
{
    public void Configure(EntityTypeBuilder<TelegramLinkCode> builder)
    {
        builder.ToTable("TelegramLinkCodes");
        builder.Property(c => c.CodeHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(c => c.CodeHash).IsUnique();
        builder.HasIndex(c => c.UserId);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("NotificationPreferences");
        builder.Property(p => p.Category).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(p => p.Channel).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.HasIndex(p => new { p.UserId, p.Category, p.Channel }).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class TelegramDialogThrottleConfiguration : IEntityTypeConfiguration<TelegramDialogThrottle>
{
    public void Configure(EntityTypeBuilder<TelegramDialogThrottle> builder)
    {
        builder.ToTable("TelegramDialogThrottles");
        builder.HasKey(t => new { t.UserId, t.ExchangeId });
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
