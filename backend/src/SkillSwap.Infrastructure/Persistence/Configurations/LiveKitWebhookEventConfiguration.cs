using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class LiveKitWebhookEventConfiguration : IEntityTypeConfiguration<LiveKitWebhookEvent>
{
    /// <summary>Пределы длины полей: и в БД, и при разборе webhook (длиннее — запрос отклоняется).</summary>
    public const int EventIdMaxLength = 128;
    public const int TypeMaxLength = 64;
    public const int RoomNameMaxLength = 128;
    public const int IdentityMaxLength = 256;

    public void Configure(EntityTypeBuilder<LiveKitWebhookEvent> builder)
    {
        builder.ToTable("LiveKitWebhookEvents");

        builder.Property(e => e.EventId).HasMaxLength(EventIdMaxLength).IsRequired();
        builder.Property(e => e.Type).HasMaxLength(TypeMaxLength).IsRequired();
        builder.Property(e => e.RoomName).HasMaxLength(RoomNameMaxLength);
        builder.Property(e => e.ParticipantIdentity).HasMaxLength(IdentityMaxLength);

        // Идемпотентность (раздел 11.1 ТЗ): повторная доставка того же события упирается в этот индекс и игнорируется.
        builder.HasIndex(e => e.EventId).IsUnique();
    }
}
