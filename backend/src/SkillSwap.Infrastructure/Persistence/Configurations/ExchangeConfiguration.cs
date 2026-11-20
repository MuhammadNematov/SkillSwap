using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class ExchangeConfiguration : IEntityTypeConfiguration<Exchange>
{
    public void Configure(EntityTypeBuilder<Exchange> builder)
    {
        builder.ToTable("Exchanges", t =>
        {
            t.HasCheckConstraint("CK_Exchanges_DifferentUsers", "\"UserAId\" <> \"UserBId\"");
            t.HasCheckConstraint("CK_Exchanges_Dates", "\"EndDate\" > \"StartDate\"");
        });

        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(e => e.AbortReason).HasMaxLength(Exchange.AbortReasonMaxLength);
        builder.Property(e => e.AbortedForNoShow).HasDefaultValue(false);

        // Из одной заявки получается ровно один обмен.
        builder.HasIndex(e => e.RequestId).IsUnique();

        // «Мои обмены» и подсчёт лимита активных: по каждому из двух участников.
        builder.HasIndex(e => new { e.UserAId, e.Status });
        builder.HasIndex(e => new { e.UserBId, e.Status });

        // Фоновое завершение по сроку ищет активные обмены с истёкшим EndDate.
        builder.HasIndex(e => new { e.Status, e.EndDate });

        // Очистка чатов (раздел 10.3 ТЗ): закрытые обмены, чаты которых ещё не вычищены.
        builder.HasIndex(e => e.ClosedAt).HasFilter("\"ChatPurgedAt\" IS NULL AND \"ClosedAt\" IS NOT NULL");

        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserAId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserBId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Skill>().WithMany().HasForeignKey(e => e.SkillAId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Skill>().WithMany().HasForeignKey(e => e.SkillBId).OnDelete(DeleteBehavior.Restrict);

        // NoAction: при удалении пользователя заявка и обмен удаляются каскадом вместе, и порядок не важен.
        builder.HasOne<ExchangeRequest>().WithMany().HasForeignKey(e => e.RequestId).OnDelete(DeleteBehavior.NoAction);

        builder.Property<uint>("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();
    }
}
