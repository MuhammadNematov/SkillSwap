using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("Attachments", t => t.HasCheckConstraint("CK_Attachments_Size", "\"SizeBytes\" > 0"));

        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(a => a.StorageKey).HasMaxLength(200).IsRequired();
        builder.Property(a => a.FileName).HasMaxLength(Attachment.FileNameMaxLength).IsRequired();
        builder.Property(a => a.MimeType).HasMaxLength(Attachment.MimeTypeMaxLength).IsRequired();

        builder.HasIndex(a => a.StorageKey).IsUnique();
        builder.HasIndex(a => a.ExchangeId);

        // Удаление обмена (очисткой чатов через 90 дней) убирает записи о вложениях; сами файлы чистит та же задача.
        builder.HasOne<Exchange>().WithMany().HasForeignKey(a => a.ExchangeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.OwnerId).OnDelete(DeleteBehavior.NoAction);
    }
}
