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
        builder.HasData(
            new
            {
                Key = SystemSettingKeys.MinorsMatchOnlyMinors,
                Value = "true",
                Description = "Подбор: пользователи 16–17 лет только друг с другом, взрослые только со взрослыми (раздел 10.4 ТЗ). true или false.",
                CreatedAt = seedTime,
                UpdatedAt = seedTime,
            },
            Seed(SystemSettingKeys.MaxActiveExchanges, "3", "Антиспам: не более стольких активных обменов одновременно (раздел 5.3 ТЗ)."),
            Seed(SystemSettingKeys.MaxPendingOutgoing, "5", "Антиспам: не более стольких ожидающих исходящих заявок (раздел 5.3 ТЗ)."),
            Seed(SystemSettingKeys.MaxRequestsPerDay, "10", "Антиспам: не более стольких новых заявок за сутки (раздел 5.3 ТЗ)."),
            Seed(SystemSettingKeys.DeclineCooldownDays, "30", "Антиспам: через сколько дней после отказа можно снова отправить заявку тому же человеку (раздел 5.3 ТЗ)."));

        static object Seed(string key, string value, string description)
        {
            var time = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);
            return new { Key = key, Value = value, Description = description, CreatedAt = time, UpdatedAt = time };
        }
    }
}
