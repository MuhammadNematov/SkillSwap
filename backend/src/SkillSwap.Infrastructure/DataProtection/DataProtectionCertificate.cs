using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SkillSwap.Infrastructure.DataProtection;

/// <summary>Загрузка и проверка сертификата ключей. Любая проблема даёт понятную ошибку при старте приложения.</summary>
public static class DataProtectionCertificate
{
    public const int MinRsaKeySize = 2048;

    public static X509Certificate2 Load(DataProtectionCertificateOptions options)
    {
        var hasPath = !string.IsNullOrWhiteSpace(options.CertificatePath);
        var hasBase64 = !string.IsNullOrWhiteSpace(options.CertificateBase64);

        if (!hasPath && !hasBase64)
        {
            throw new InvalidOperationException(
                "Не задан сертификат для защиты ключей шифрования. Задайте DataProtection:CertificatePath " +
                "(или DataProtection:CertificateBase64) и DataProtection:CertificatePassword через user-secrets " +
                "или переменные окружения. Как создать сертификат: docs/BACKEND-DEV.md, раздел «Ключи шифрования».");
        }

        if (hasPath && hasBase64)
        {
            throw new InvalidOperationException(
                "Сертификат ключей задан двумя способами сразу (DataProtection:CertificatePath и DataProtection:CertificateBase64). Оставьте один.");
        }

        byte[] bytes;
        if (hasPath)
        {
            try
            {
                bytes = File.ReadAllBytes(options.CertificatePath!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Не удалось прочитать файл сертификата ключей (DataProtection:CertificatePath): {ex.Message}");
            }
        }
        else
        {
            try
            {
                bytes = Convert.FromBase64String(options.CertificateBase64!.Trim());
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("DataProtection:CertificateBase64 — это не base64.");
            }
        }

        X509Certificate2 certificate;
        try
        {
            // Эфемерный набор ключей: закрытый ключ живёт только в памяти процесса, на диск/в профиль пользователя не пишется.
            certificate = X509CertificateLoader.LoadPkcs12(bytes, options.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException(
                "Не удалось открыть сертификат ключей: неверный пароль (DataProtection:CertificatePassword) или это не файл PFX.");
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                "В сертификате ключей нет закрытого ключа. Нужен PFX с закрытым ключом, а не открытый сертификат (.cer).");
        }

        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is null || rsa.KeySize < MinRsaKeySize)
        {
            certificate.Dispose();
            throw new InvalidOperationException(
                $"Сертификат ключей должен быть на RSA не короче {MinRsaKeySize} бит.");
        }

        return certificate;
    }
}
