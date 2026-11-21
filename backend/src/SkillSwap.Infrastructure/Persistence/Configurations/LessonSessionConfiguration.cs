using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class LessonSessionConfiguration : IEntityTypeConfiguration<LessonSession>
{
    public void Configure(EntityTypeBuilder<LessonSession> builder)
    {
        builder.ToTable("Sessions", t =>
        {
            t.HasCheckConstraint("CK_Sessions_Duration", "\"DurationMin\" > 0");
            t.HasCheckConstraint("CK_Sessions_Times", "\"EndAt\" > \"StartAt\"");
        });

        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.Topic).HasMaxLength(LessonSession.TopicMaxLength);

        // Раздел 13 ТЗ: индекс (обмен, начало). Календарь и проверка пересечений ищут по статусу и времени.
        builder.HasIndex(s => new { s.ExchangeId, s.StartAt });
        builder.HasIndex(s => new { s.Status, s.StartAt });
        builder.HasIndex(s => s.StartAt);   // статистика (этап 7, шаг 8): уроки за период

        builder.HasOne<Exchange>().WithMany().HasForeignKey(s => s.ExchangeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.TeacherId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<User>().WithMany().HasForeignKey(s => s.ProposedById).OnDelete(DeleteBehavior.NoAction);

        // Оптимистичная блокировка по xmin (раздел 10.2 ТЗ): два одновременных ответа на один слот не пройдут оба.
        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}
