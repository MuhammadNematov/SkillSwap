using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.ToTable("SystemSettings");

        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(SystemSetting.KeyMaxLength);
        builder.Property(s => s.Value).HasMaxLength(SystemSetting.ValueMaxLength).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(500).IsRequired();

        // Значения по умолчанию кладёт миграция. Дальше ими владеет суперадмин: правка миграцией их не перезапишет.
        var seedTime = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        builder.HasData(new
        {
            Key = SystemSettingKeys.MinorsMatchOnlyMinors,
            Value = "true",
            Description = "Подбор: пользователи 16–17 лет только друг с другом, взрослые только со взрослыми (раздел 10.4 ТЗ). true или false.",
            CreatedAt = seedTime,
            UpdatedAt = seedTime,
        });
    }
}
