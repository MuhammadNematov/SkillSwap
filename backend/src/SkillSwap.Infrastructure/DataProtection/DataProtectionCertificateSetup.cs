using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace SkillSwap.Infrastructure.DataProtection;

public static class DataProtectionCertificateSetup
{
    /// <summary>
    /// Защищает ключи шифрования сертификатом из настроек: в хранилище (БД) ключи лежат только в зашифрованном виде,
    /// расшифровать их без сертификата нельзя. Сертификат читается лениво и один раз; ошибку в настройках показывает
    /// проверка при старте приложения.
    /// </summary>
    public static IDataProtectionBuilder ProtectKeysWithConfiguredCertificate(
        this IDataProtectionBuilder builder, IConfiguration configuration)
    {
        var services = builder.Services;

        services.AddOptions<DataProtectionCertificateOptions>()
            .Bind(configuration.GetSection(DataProtectionCertificateOptions.SectionName));
        services.AddSingleton<DataProtectionCertificateProvider>();
        services.AddHostedService<DataProtectionCertificateCheck>();

        // Стандартные расширения принимают готовый сертификат, а он здесь появляется только после чтения настроек.
        // Поэтому применяем их к временному набору служб и переносим результат в настоящий.
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
        {
            var certificate = sp.GetRequiredService<DataProtectionCertificateProvider>().Certificate;
            var temporary = new ServiceCollection();
            temporary.AddSingleton(sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance);
            temporary.AddDataProtection().ProtectKeysWithCertificate(certificate);
            using var temporaryProvider = temporary.BuildServiceProvider();
            var encryptor = temporaryProvider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor;
            return new ConfigureOptions<KeyManagementOptions>(options => options.XmlEncryptor = encryptor);
        });

        // Расшифровка: сертификат не из хранилища ОС нужно назвать явно, иначе после перезапуска ключи не прочитать.
        // Тип настроек расшифровки скрыт внутри библиотеки, поэтому переносим готовую регистрацию целиком.
        // Какие именно регистрации добавляет вызов, узнаём на пробном запуске без сертификатов; настоящие значения
        // (с загруженным сертификатом) подставляются при создании служб.
        foreach (var shape in CaptureOptionsRegistrations(b => b.UnprotectKeysWithAnyCertificate()))
        {
            var serviceType = shape.ServiceType;
            services.Add(new ServiceDescriptor(serviceType, sp =>
            {
                var certificate = sp.GetRequiredService<DataProtectionCertificateProvider>().Certificate;
                return CaptureOptionsRegistrations(b => b.UnprotectKeysWithAnyCertificate(certificate))
                    .First(d => d.ServiceType == serviceType).ImplementationInstance!;
            }, ServiceLifetime.Singleton));
        }

        return builder;
    }

    /// <summary>Какие настройки (IConfigureOptions) добавляет в службы указанный вызов расширения.</summary>
    private static List<ServiceDescriptor> CaptureOptionsRegistrations(Action<IDataProtectionBuilder> apply)
    {
        var services = new ServiceCollection();
        var dataProtection = services.AddDataProtection();
        var before = services.ToHashSet();
        apply(dataProtection);
        return services
            .Where(d => !before.Contains(d)
                && d.ServiceType.IsGenericType
                && d.ServiceType.GetGenericTypeDefinition() == typeof(IConfigureOptions<>)
                && d.ImplementationInstance is not null)
            .ToList();
    }
}

/// <summary>Загружает сертификат один раз на всё приложение.</summary>
public sealed class DataProtectionCertificateProvider
{
    private readonly Lazy<X509Certificate2> _certificate;

    public DataProtectionCertificateProvider(IOptions<DataProtectionCertificateOptions> options) =>
        _certificate = new Lazy<X509Certificate2>(() => DataProtectionCertificate.Load(options.Value));

    public X509Certificate2 Certificate => _certificate.Value;
}

/// <summary>Fail fast: без исправного сертификата приложение не стартует (иначе ключи писались бы открытым текстом).</summary>
public sealed class DataProtectionCertificateCheck : IHostedService
{
    private static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(30);

    private readonly DataProtectionCertificateProvider _provider;
    private readonly TimeProvider _time;
    private readonly ILogger<DataProtectionCertificateCheck> _logger;

    public DataProtectionCertificateCheck(
        DataProtectionCertificateProvider provider, TimeProvider time, ILogger<DataProtectionCertificateCheck> logger)
    {
        _provider = provider;
        _time = time;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var certificate = _provider.Certificate;   // бросит понятную ошибку, если что-то не так
        var notAfter = certificate.NotAfter.ToUniversalTime();
        var left = notAfter - _time.GetUtcNow().UtcDateTime;

        // Срок сертификата не обрывает работу: он нужен и для расшифровки уже созданных ключей.
        // Но заранее напоминаем, что пора выпускать новый (и хранить старый).
        if (left <= TimeSpan.Zero)
        {
            _logger.LogError("Сертификат ключей шифрования ({Thumbprint}) просрочен {Date:u}.", certificate.Thumbprint, notAfter);
        }
        else if (left <= ExpiryWarning)
        {
            _logger.LogWarning("Сертификат ключей шифрования ({Thumbprint}) истекает {Date:u}.", certificate.Thumbprint, notAfter);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
