using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Infrastructure.DataProtection;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>
/// Ключи шифрования (раздел 10.3 ТЗ): в хранилище лежат только зашифрованными сертификатом, после перезапуска
/// приложения данные расшифровываются, а без нужного сертификата — нет.
/// </summary>
[Collection(ApiCollection.Name)]
public class DataProtectionCertificateTests : IDisposable
{
    private readonly ApiFactory _factory;
    private readonly string _keysDirectory = Path.Combine(Path.GetTempPath(), "skillswap-dp-" + Guid.NewGuid().ToString("N"));

    public DataProtectionCertificateTests(ApiFactory factory) => _factory = factory;

    public void Dispose()
    {
        if (Directory.Exists(_keysDirectory))
        {
            Directory.Delete(_keysDirectory, recursive: true);
        }
    }

    // ---------- настоящее приложение и БД ----------

    [Fact]
    public async Task Keys_in_the_database_are_encrypted_and_data_survives_an_application_restart()
    {
        var protector = _factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test");
        var secret = "тайное сообщение " + Guid.NewGuid();
        var sealedText = protector.Protect(secret);

        var keys = await _factory.QueryAsync(db => db.Set<DataProtectionKey>().Select(k => k.Xml!).ToListAsync());
        Assert.NotEmpty(keys);
        Assert.All(keys, xml =>
        {
            Assert.Contains("EncryptedData", xml);          // зашифрованный ключ
            Assert.DoesNotContain("masterKey", xml);        // открытого ключа в БД нет
        });

        // «Перезапуск»: новый экземпляр приложения на той же БД, ключи читаются из хранилища и расшифровываются сертификатом.
        using var restarted = _factory.WithSettings();
        var again = restarted.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test");
        Assert.Equal(secret, again.Unprotect(sealedText));

        var after = await _factory.QueryAsync(db => db.Set<DataProtectionKey>().CountAsync());
        Assert.Equal(keys.Count, after);                    // новый ключ при перезапуске не создаётся
    }

    [Fact]
    public void The_application_does_not_start_without_a_certificate()
    {
        using var app = _factory.WithSettings(null, ("DataProtection:CertificateBase64", ""));

        Assert.Contains("Не задан сертификат", StartupError(() => _ = app.Services));
    }

    [Fact]
    public void The_application_does_not_start_with_a_wrong_password()
    {
        using var app = _factory.WithSettings(null, ("DataProtection:CertificatePassword", "not-the-password"));

        Assert.Contains("неверный пароль", StartupError(() => _ = app.Services));
    }

    [Fact]
    public void The_application_does_not_start_when_the_certificate_is_given_twice()
    {
        using var app = _factory.WithSettings(null, ("DataProtection:CertificatePath", "/some/file.pfx"));

        Assert.Contains("двумя способами", StartupError(() => _ = app.Services));
    }

    private static string StartupError(Action start)
    {
        var ex = Assert.ThrowsAny<Exception>(start);
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is InvalidOperationException && e.Message.Contains("ключ", StringComparison.OrdinalIgnoreCase))
            {
                return e.Message;
            }
        }
        throw new Xunit.Sdk.XunitException("Ожидалась понятная ошибка про сертификат ключей, а получено: " + ex);
    }

    // ---------- сам механизм на отдельном хранилище ----------

    private IDataProtectionProvider Start(TestPfx pfx, bool asFile = false)
    {
        var settings = new Dictionary<string, string?> { ["DataProtection:CertificatePassword"] = pfx.Password };
        if (asFile)
        {
            var path = Path.Combine(_keysDirectory, "cert-" + Guid.NewGuid().ToString("N") + ".pfx");
            Directory.CreateDirectory(_keysDirectory);
            File.WriteAllBytes(path, pfx.Bytes);
            settings["DataProtection:CertificatePath"] = path;
        }
        else
        {
            settings["DataProtection:CertificateBase64"] = pfx.Base64;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
            .SetApplicationName("SkillSwap")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_keysDirectory, "keys")))
            .ProtectKeysWithConfiguredCertificate(configuration);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    private string[] KeyFiles() => Directory.GetFiles(Path.Combine(_keysDirectory, "keys"), "*.xml");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // тот же сертификат, но из файла, как в разработке
    public void Keys_are_written_encrypted_and_readable_after_a_restart(bool asFile)
    {
        var pfx = TestCertificates.Create();
        var sealedText = Start(pfx, asFile).CreateProtector("chat").Protect("привет");

        var files = KeyFiles();
        Assert.NotEmpty(files);
        Assert.All(files, f =>
        {
            var xml = File.ReadAllText(f);
            Assert.Contains("EncryptedData", xml);
            Assert.DoesNotContain("masterKey", xml);
        });

        Assert.Equal("привет", Start(pfx, asFile).CreateProtector("chat").Unprotect(sealedText));
        Assert.Equal(files.Length, KeyFiles().Length);   // ключ не пересоздан
    }

    [Fact]
    public void Another_certificate_cannot_read_the_data()
    {
        var sealedText = Start(TestCertificates.Create()).CreateProtector("chat").Protect("привет");

        var other = Start(TestCertificates.Create());   // другой сертификат: потеря/подмена сертификата = потеря данных

        Assert.ThrowsAny<CryptographicException>(() => other.CreateProtector("chat").Unprotect(sealedText));
    }

    // ---------- загрузка сертификата ----------

    [Fact]
    public void A_certificate_without_a_private_key_is_rejected()
    {
        var publicOnly = TestCertificates.Create(withPrivateKey: false);

        var ex = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificate.Load(
            new DataProtectionCertificateOptions { CertificateBase64 = publicOnly.Base64, CertificatePassword = publicOnly.Password }));
        Assert.Contains("закрытого ключа", ex.Message);
    }

    [Fact]
    public void A_short_rsa_key_is_rejected()
    {
        var weak = TestCertificates.Create(keySize: 1024);

        var ex = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificate.Load(
            new DataProtectionCertificateOptions { CertificateBase64 = weak.Base64, CertificatePassword = weak.Password }));
        Assert.Contains("2048", ex.Message);
    }

    [Theory]
    [InlineData("%%% not base64 %%%")]
    [InlineData("aGVsbG8=")]   // base64, но это не PFX
    public void Garbage_instead_of_a_certificate_is_rejected(string value)
    {
        Assert.Throws<InvalidOperationException>(() => DataProtectionCertificate.Load(
            new DataProtectionCertificateOptions { CertificateBase64 = value }));
    }

    [Fact]
    public void A_missing_file_is_reported_clearly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificate.Load(
            new DataProtectionCertificateOptions { CertificatePath = Path.Combine(_keysDirectory, "nope.pfx") }));
        Assert.Contains("CertificatePath", ex.Message);
    }
}
