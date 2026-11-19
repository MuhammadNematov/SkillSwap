using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class SessionAttendanceConfiguration : IEntityTypeConfiguration<SessionAttendance>
{
    public void Configure(EntityTypeBuilder<SessionAttendance> builder)
    {
        builder.ToTable("SessionAttendances", t =>
            t.HasCheckConstraint("CK_SessionAttendances_Interval", "\"LeftAt\" IS NULL OR \"LeftAt\" >= \"JoinedAt\""));

        builder.HasIndex(a => new { a.SessionId, a.UserId });

        // У участника в уроке не больше одного открытого интервала: двойной participant_joined (повторная доставка, гонка) его не удвоит.
        builder.HasIndex(a => new { a.SessionId, a.UserId }).IsUnique().HasFilter("\"LeftAt\" IS NULL")
            .HasDatabaseName("IX_SessionAttendances_OpenPerUser");

        builder.HasOne<LessonSession>().WithMany().HasForeignKey(a => a.SessionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.NoAction);
    }
}
