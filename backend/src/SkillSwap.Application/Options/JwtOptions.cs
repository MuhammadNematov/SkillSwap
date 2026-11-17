using System.ComponentModel.DataAnnotations;

namespace SkillSwap.Application.Options;

/// <summary>
/// Настройки JWT, секция "Jwt". SigningKey — секрет: только user-secrets или переменная окружения
/// Jwt__SigningKey, никогда не в репозитории.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "SkillSwap";

    [Required]
    public string Audience { get; set; } = "SkillSwap";

    /// <summary>Ключ подписи HS256, не короче 32 символов.</summary>
    [Required, MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Access JWT живёт 15 минут (раздел 10.1 ТЗ).</summary>
    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Refresh-токен живёт 30 дней (раздел 10.1 ТЗ).</summary>
    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 30;
}
