using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");

        builder.Property(m => m.Type).HasConversion<string>().HasMaxLength(32).IsRequired();

        // Текст только в зашифрованном виде (Data Protection добавляет служебные байты и кодирует в base64: запас от 4000 символов).
        builder.Property(m => m.EncryptedContent).HasMaxLength(32000);

        // История обмена с курсором (время, id) и подсчёт непрочитанных (раздел 13 ТЗ: индекс по обмену и времени).
        builder.HasIndex(m => new { m.ExchangeId, m.CreatedAt, m.Id });

        // Удаление обмена (в том числе очисткой чатов через 90 дней) убирает его сообщения.
        builder.HasOne<Exchange>().WithMany().HasForeignKey(m => m.ExchangeId).OnDelete(DeleteBehavior.Cascade);

        // Автор: NoAction, потому что при удалении пользователя сообщения уйдут каскадом вместе с обменом.
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.NoAction);

        // Правка и удаление одновременно: второй получит 409.
        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}

public class MessageRevisionConfiguration : IEntityTypeConfiguration<MessageRevision>
{
    public void Configure(EntityTypeBuilder<MessageRevision> builder)
    {
        builder.ToTable("MessageRevisions");

        builder.Property(r => r.PreviousEncryptedContent).HasMaxLength(32000).IsRequired();
        builder.HasIndex(r => new { r.MessageId, r.ChangedAt });
        builder.HasOne<Message>().WithMany().HasForeignKey(r => r.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}
