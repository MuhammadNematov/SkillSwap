using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");

        builder.Property(n => n.Type).HasConversion<string>().HasMaxLength(64).IsRequired();
        builder.Property(n => n.PayloadJson).HasColumnType("jsonb").IsRequired();

        // Список «мои уведомления», «только непрочитанные» и счётчик для значка (раздел 13 ТЗ).
        builder.HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt });
        builder.HasIndex(n => new { n.IsRead, n.CreatedAt });   // ночная очистка старых уведомлений (этап 7, шаг 9)

        // Одно непрочитанное уведомление «новые сообщения» на обмен и получателя: защита от дублей при одновременных сообщениях.
        builder.HasIndex(n => new { n.UserId, n.ExchangeId }).IsUnique().HasFilter("\"Type\" = 'NewMessages' AND NOT \"IsRead\"");

        builder.HasOne<User>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
