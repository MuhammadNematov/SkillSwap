using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>Самоподписанные сертификаты для тестов ключей шифрования (в репозитории настоящих сертификатов нет).</summary>
public sealed record TestPfx(byte[] Bytes, string Password)
{
    public string Base64 => Convert.ToBase64String(Bytes);
}

public static class TestCertificates
{
    /// <summary>Сертификат, которым защищены ключи во всех тестах приложения.</summary>
    public static TestPfx Shared { get; } = Create("CN=SkillSwap test keys");

    public static TestPfx Create(string subject = "CN=SkillSwap test keys", int keySize = 2048, bool withPrivateKey = true,
        string password = "test-pfx-password")
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(365));
        if (withPrivateKey)
        {
            return new TestPfx(certificate.Export(X509ContentType.Pfx, password), password);
        }

        // PFX, в котором лежит только открытый сертификат (без закрытого ключа).
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        return new TestPfx(publicOnly.Export(X509ContentType.Pfx, password), password);
    }
}
