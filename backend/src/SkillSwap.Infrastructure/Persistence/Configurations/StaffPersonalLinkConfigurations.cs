using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class StaffPersonalLinkConfiguration : IEntityTypeConfiguration<StaffPersonalLink>
{
    public void Configure(EntityTypeBuilder<StaffPersonalLink> builder)
    {
        builder.ToTable("StaffPersonalLinks");

        // История привязок сотрудника (проверка конфликта интересов) и «какие сотрудники когда-либо были привязаны к этому личному аккаунту».
        builder.HasIndex(l => l.StaffId);
        builder.HasIndex(l => l.PersonalUserId);

        // Одна действующая привязка на сотрудника и один действующий служебный на личный аккаунт.
        builder.HasIndex(l => l.StaffId).IsUnique().HasFilter("\"UnlinkedAt\" IS NULL").HasDatabaseName("IX_StaffPersonalLinks_Active_Staff");
        builder.HasIndex(l => l.PersonalUserId).IsUnique().HasFilter("\"UnlinkedAt\" IS NULL").HasDatabaseName("IX_StaffPersonalLinks_Active_Personal");

        // Историю не теряем даже при удалении аккаунтов: ограничение, а не каскад.
        builder.HasOne<User>().WithMany().HasForeignKey(l => l.StaffId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(l => l.PersonalUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(l => l.UnlinkedById).OnDelete(DeleteBehavior.Restrict);

        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}

public class PersonalUnlinkRequestConfiguration : IEntityTypeConfiguration<PersonalUnlinkRequest>
{
    public void Configure(EntityTypeBuilder<PersonalUnlinkRequest> builder)
    {
        builder.ToTable("PersonalUnlinkRequests");

        builder.Property(r => r.Reason).HasMaxLength(PersonalUnlinkRequest.ReasonMaxLength).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);

        builder.HasIndex(r => new { r.Status, r.CreatedAt });

        // Не больше одного необработанного запроса на сотрудника.
        builder.HasIndex(r => r.StaffId).IsUnique().HasFilter("\"Status\" = 'Pending'").HasDatabaseName("IX_PersonalUnlinkRequests_Pending_Staff");

        builder.HasOne<User>().WithMany().HasForeignKey(r => r.StaffId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.DecidedById).OnDelete(DeleteBehavior.Restrict);

        builder.Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
    }
}
