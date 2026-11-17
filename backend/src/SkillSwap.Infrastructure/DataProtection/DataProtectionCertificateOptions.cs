namespace SkillSwap.Infrastructure.DataProtection;

/// <summary>
/// Сертификат, которым защищены ключи шифрования (Data Protection, раздел 10.3 ТЗ).
/// Нужно задать ровно один из способов: файл (удобно в разработке) или base64 содержимого PFX
/// (удобно для переменных окружения на боевом сервере). Пароль и содержимое сертификата в репозиторий не попадают.
/// </summary>
public sealed class DataProtectionCertificateOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>Путь к файлу PFX. Окружение: DataProtection__CertificatePath.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Содержимое PFX в base64. Окружение: DataProtection__CertificateBase64.</summary>
    public string? CertificateBase64 { get; set; }

    /// <summary>Пароль PFX. Окружение: DataProtection__CertificatePassword.</summary>
    public string? CertificatePassword { get; set; }
}
