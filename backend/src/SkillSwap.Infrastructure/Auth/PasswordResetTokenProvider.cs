using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Infrastructure.Auth;

/// <summary>Срок жизни ссылки сброса пароля: 1 час (FR-4 ТЗ).</summary>
public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public const int LifetimeHours = 1;

    public PasswordResetTokenProviderOptions()
    {
        // Отдельное имя = отдельный «защитник» Data Protection: токен сброса не совпадёт с токеном подтверждения email.
        Name = "PasswordResetTokenProvider";
        TokenLifespan = TimeSpan.FromHours(LifetimeHours);
    }
}

/// <summary>
/// Поставщик токенов сброса пароля. Стандартный поставщик Identity один на все задачи и живёт 1 сутки;
/// если сократить его срок, ссылка подтверждения email тоже станет часовой. Поэтому для сброса пароля
/// заведён свой поставщик с собственным сроком.
/// </summary>
public sealed class PasswordResetTokenProvider : DataProtectorTokenProvider<User>
{
    public const string ProviderName = "PasswordReset";

    public PasswordResetTokenProvider(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<PasswordResetTokenProviderOptions> options,
        ILogger<DataProtectorTokenProvider<User>> logger)
        : base(dataProtectionProvider, options, logger)
    {
    }
}
