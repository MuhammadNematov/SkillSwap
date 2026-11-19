using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.Property(u => u.Name).HasMaxLength(100).IsRequired();
        builder.Property(u => u.AvatarKey).HasMaxLength(200);
        builder.Property(u => u.ErasurePending).HasDefaultValue(false);
        builder.Property(u => u.Bio).HasMaxLength(1000);

        // Enum хранятся строками varchar(32) (раздел 13 ТЗ).
        builder.Property(u => u.AccountType).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(32).IsRequired();

        builder.Property(u => u.UiLanguage).HasMaxLength(8).IsRequired();
        builder.Property(u => u.TimeZone).HasMaxLength(64).IsRequired();
        builder.Property(u => u.RatingAvg).HasPrecision(3, 2).HasDefaultValue(0m);
        builder.Property(u => u.RatingCount).HasDefaultValue(0);
        builder.Property(u => u.HiddenByUser).HasDefaultValue(false);

        // Последний шаг TOTP меняет только TwoFactorStepStore одним атомарным UPDATE. Обычные сохранения
        // (UserManager.UpdateAsync помечает ВСЕ поля изменёнными) не должны его трогать: иначе устаревшее
        // значение из памяти перезапишет свежий шаг и повторное использование кода снова станет возможным.
        builder.Property(u => u.TwoFactorLastUsedStep)
            .Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);

        // Так же и LastActiveAt: его пишет только UserActivityStore точечным UPDATE.
        builder.Property(u => u.LastActiveAt)
            .Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);

        // Не более одного служебного аккаунта на личный (раздел 3 ТЗ).
        builder.HasIndex(u => u.LinkedUserId).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(u => u.LinkedUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
