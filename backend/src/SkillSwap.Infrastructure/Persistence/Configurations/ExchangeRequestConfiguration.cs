using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class ExchangeRequestConfiguration : IEntityTypeConfiguration<ExchangeRequest>
{
    public void Configure(EntityTypeBuilder<ExchangeRequest> builder)
    {
        builder.ToTable("ExchangeRequests", t =>
        {
            t.HasCheckConstraint("CK_ExchangeRequests_NotSelf", "\"FromUserId\" <> \"ToUserId\"");
            t.HasCheckConstraint("CK_ExchangeRequests_Duration", "\"DurationDays\" IN (30, 60)");
        });

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.Message).HasMaxLength(ExchangeRequest.MessageMaxLength);

        // Ящики «входящие» и «исходящие» и подсчёт лимитов: по пользователю и статусу, новые сверху.
        builder.HasIndex(r => new { r.FromUserId, r.Status, r.CreatedAt });
        builder.HasIndex(r => new { r.ToUserId, r.Status, r.CreatedAt });

        // Удаление пользователя убирает его заявки; навык из каталога удалить нельзя, пока на него есть заявки (его выключают).
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.FromUserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.ToUserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Skill>().WithMany().HasForeignKey(r => r.FromTeachesSkillId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Skill>().WithMany().HasForeignKey(r => r.ToTeachesSkillId).OnDelete(DeleteBehavior.Restrict);

        // Оптимистичная блокировка по xmin (раздел 10.2 ТЗ): принять и отозвать одновременно — второй получит 409.
        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
