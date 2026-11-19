using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

/// <summary>
/// Таблицы отзывов и обращений (раздел 13 ТЗ). Пользователей физически не удаляют (при удалении аккаунта личные данные затираются), поэтому внешние ключи на них
/// «Restrict»: отзывы и обращения остаются, подпись автора меняется на «Удалённый пользователь».
/// </summary>
public class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> builder)
    {
        builder.ToTable("Reviews", t =>
        {
            t.HasCheckConstraint("CK_Reviews_Scores", "\"ScoreKnowledge\" BETWEEN 1 AND 5 AND \"ScoreExplaining\" BETWEEN 1 AND 5 AND \"ScorePunctuality\" BETWEEN 1 AND 5 AND \"ScorePoliteness\" BETWEEN 1 AND 5");
            t.HasCheckConstraint("CK_Reviews_NotSelf", "\"AuthorId\" <> \"TargetId\"");
        });

        builder.Property(r => r.Text).HasMaxLength(Review.TextMaxLength).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        // По разделу 13 ТЗ: один отзыв автора на обмен.
        builder.HasIndex(r => new { r.ExchangeId, r.AuthorId }).IsUnique();

        // Отзывы о человеке (профиль и пересчёт рейтинга) и очередь слепой публикации.
        builder.HasIndex(r => new { r.TargetId, r.Status });
        builder.HasIndex(r => new { r.Status, r.CreatedAt });

        builder.HasOne<Exchange>().WithMany().HasForeignKey(r => r.ExchangeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.AuthorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.TargetId).OnDelete(DeleteBehavior.Restrict);

        // Публикация, заморозка по спору и скрытие меняют отзыв из разных запросов: оптимистичная блокировка по xmin (раздел 10.2 ТЗ).
        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}

public class CaseConfiguration : IEntityTypeConfiguration<Case>
{
    public void Configure(EntityTypeBuilder<Case> builder)
    {
        builder.ToTable("Cases", t => t.HasCheckConstraint("CK_Cases_NotSelf", "\"OpenedById\" <> \"AgainstUserId\""));

        builder.Property(c => c.Type).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(c => c.Category).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(c => c.Priority).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(c => c.Decision).HasConversion<string>().HasMaxLength(32);
        builder.Property(c => c.Description).HasMaxLength(CaseRules.DescriptionMaxLength).IsRequired();
        builder.Property(c => c.DecisionReason).HasMaxLength(CaseRules.ReasonMaxLength);

        // Очередь модератора: статус, приоритет, срок реакции. Свои обращения пользователя и обращения на него.
        builder.HasIndex(c => new { c.Status, c.Priority, c.DueAt });
        builder.HasIndex(c => new { c.OpenedById, c.CreatedAt });
        builder.HasIndex(c => new { c.AgainstUserId, c.CreatedAt });
        builder.HasIndex(c => new { c.ExchangeId, c.Status });
        builder.HasIndex(c => c.ModeratorId);

        // Таймеры: ответ в течение 48 часов (WaitingResponse) и закрытие через 7 дней после решения (Resolved).
        builder.HasIndex(c => c.ResponseDeadlineAt).HasFilter("\"Status\" = 'WaitingResponse'");
        builder.HasIndex(c => c.ResolvedAt).HasFilter("\"Status\" = 'Resolved'");

        builder.HasOne<Exchange>().WithMany().HasForeignKey(c => c.ExchangeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Review>().WithMany().HasForeignKey(c => c.ReviewId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.OpenedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.AgainstUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.ModeratorId).OnDelete(DeleteBehavior.Restrict);

        // Двое модераторов не возьмут одно обращение: второй получит конфликт (раздел 10.2 ТЗ).
        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}

public class CaseEvidenceConfiguration : IEntityTypeConfiguration<CaseEvidence>
{
    public void Configure(EntityTypeBuilder<CaseEvidence> builder)
    {
        builder.ToTable("CaseEvidence");
        builder.HasKey(e => new { e.CaseId, e.MessageId });
        builder.HasIndex(e => e.MessageId);

        builder.HasOne<Case>().WithMany().HasForeignKey(e => e.CaseId).OnDelete(DeleteBehavior.Cascade);

        // Приложенное сообщение удалить нельзя: очистка чата (через 90 дней) упрётся в этот ключ, а не потеряет доказательство (раздел 10.3 ТЗ).
        builder.HasOne<Message>().WithMany().HasForeignKey(e => e.MessageId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class CaseMessageConfiguration : IEntityTypeConfiguration<CaseMessage>
{
    public void Configure(EntityTypeBuilder<CaseMessage> builder)
    {
        builder.ToTable("CaseMessages");
        builder.Property(m => m.Text).HasMaxLength(CaseMessage.TextMaxLength).IsRequired();
        builder.HasIndex(m => new { m.CaseId, m.CreatedAt });
        builder.HasIndex(m => new { m.RecipientId, m.CaseId });

        builder.HasOne<Case>().WithMany().HasForeignKey(m => m.CaseId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.RecipientId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CaseMessage>().WithMany().HasForeignKey(m => m.InReplyToId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class CaseAppealConfiguration : IEntityTypeConfiguration<CaseAppeal>
{
    public void Configure(EntityTypeBuilder<CaseAppeal> builder)
    {
        builder.ToTable("CaseAppeals");
        builder.Property(a => a.Reason).HasMaxLength(CaseAppeal.ReasonMaxLength).IsRequired();
        builder.Property(a => a.ReviewerConclusion).HasMaxLength(CaseAppeal.ConclusionMaxLength);
        builder.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.FinalDecision).HasConversion<string>().HasMaxLength(32);

        // Апелляция одна на обращение (раздел 8.2 ТЗ).
        builder.HasIndex(a => a.CaseId).IsUnique();
        builder.HasIndex(a => a.ReviewerModeratorId);

        builder.HasOne<Case>().WithMany().HasForeignKey(a => a.CaseId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.AppellantId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.AdminId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.ReviewerModeratorId).OnDelete(DeleteBehavior.Restrict);

        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}

public class ChatAccessGrantConfiguration : IEntityTypeConfiguration<ChatAccessGrant>
{
    public void Configure(EntityTypeBuilder<ChatAccessGrant> builder)
    {
        builder.ToTable("ChatAccessGrants");
        builder.Property(g => g.Reason).HasConversion<string>().HasMaxLength(16).IsRequired();

        // Проверка доступа к чату идёт по (модератор, обмен) среди неотозванных: частичный индекс делает её быстрой, а «не больше одного
        // активного доступа на обращение и модератора» — гарантией на уровне БД.
        builder.HasIndex(g => new { g.ModeratorId, g.ExchangeId }).HasFilter("\"RevokedAt\" IS NULL");
        builder.HasIndex(g => new { g.CaseId, g.ModeratorId }).IsUnique().HasFilter("\"RevokedAt\" IS NULL").HasDatabaseName("IX_ChatAccessGrants_OneActivePerCaseAndModerator");

        builder.HasOne<Case>().WithMany().HasForeignKey(g => g.CaseId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(g => g.ModeratorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Exchange>().WithMany().HasForeignKey(g => g.ExchangeId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class WarningConfiguration : IEntityTypeConfiguration<Warning>
{
    public void Configure(EntityTypeBuilder<Warning> builder)
    {
        builder.ToTable("Warnings");
        builder.Property(w => w.Reason).HasMaxLength(CaseRules.ReasonMaxLength).IsRequired();

        // Действующие предупреждения пользователя (счёт до трёх): по пользователю и сроку.
        builder.HasIndex(w => new { w.UserId, w.ExpiresAt });
        builder.HasIndex(w => w.CaseId);

        builder.HasOne<User>().WithMany().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Case>().WithMany().HasForeignKey(w => w.CaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(w => w.IssuedById).OnDelete(DeleteBehavior.Restrict);
    }
}

public class BanRequestConfiguration : IEntityTypeConfiguration<BanRequest>
{
    public void Configure(EntityTypeBuilder<BanRequest> builder)
    {
        builder.ToTable("BanRequests");
        builder.Property(b => b.Reason).HasMaxLength(CaseRules.ReasonMaxLength).IsRequired();
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(16).IsRequired();

        builder.HasIndex(b => new { b.Status, b.CreatedAt });
        builder.HasIndex(b => new { b.UserId, b.Status });

        builder.HasOne<User>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Case>().WithMany().HasForeignKey(b => b.CaseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.RequestedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.DecidedById).OnDelete(DeleteBehavior.Restrict);

        // Админ решает по запросу один раз: два одновременных решения не пройдут оба.
        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Action).HasMaxLength(AuditLog.ActionMaxLength).IsRequired();
        builder.Property(a => a.TargetType).HasMaxLength(AuditLog.TargetTypeMaxLength).IsRequired();
        builder.Property(a => a.DetailsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(a => a.IpAddress).HasMaxLength(AuditLog.IpMaxLength);

        // Журнал смотрят по исполнителю и по объекту, новые сверху. Внешних ключей нет намеренно: запись об удалённом объекте остаётся.
        builder.HasIndex(a => new { a.ActorId, a.CreatedAt });
        builder.HasIndex(a => new { a.TargetType, a.TargetId, a.CreatedAt });
        builder.HasIndex(a => new { a.Action, a.CreatedAt });
    }
}
