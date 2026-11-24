using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace SkillSwap.Api.Middleware;

/// <summary>
/// Настройки доверия к заголовкам обратного прокси (секция <c>ForwardedHeaders</c>). За прокси (nginx, Vite в разработке) сервер видит адрес самого прокси, и без
/// <c>X-Forwarded-For</c> все пользователи для лимитов, сеансов, аудита и защиты входа были бы «одним клиентом».
/// Заголовки принимаются <b>только от известных прокси</b>: от остальных их присылает кто угодно, и подделанный <c>X-Forwarded-For</c> обошёл бы лимиты и подменил адрес в аудите.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Адреса прокси (например, <c>10.0.0.5</c>, <c>::1</c>).</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Сети прокси в виде CIDR (например, <c>10.0.0.0/24</c>), если адрес прокси меняется в пределах сети.</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>
    /// Сколько прокси в цепочке перед приложением (по умолчанию 1). Читается столько записей с правого края <c>X-Forwarded-For</c>: левее стоит то, что клиент
    /// мог дописать сам, а правее всех стоит запись ближайшего к приложению прокси.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;

    public bool HasProxies => KnownProxies.Length > 0 || KnownNetworks.Length > 0;

    /// <summary>Разбор настроек: неверный адрес или сеть останавливают запуск с понятной ошибкой, а не молча отключают защиту.</summary>
    public (IReadOnlyList<IPAddress> Proxies, IReadOnlyList<IPNetwork> Networks) Parse()
    {
        if (ForwardLimit < 1)
        {
            throw new InvalidOperationException("ForwardedHeaders:ForwardLimit должен быть не меньше 1.");
        }

        var proxies = new List<IPAddress>();
        foreach (var text in KnownProxies)
        {
            if (!IPAddress.TryParse(text?.Trim(), out var address))
            {
                throw new InvalidOperationException($"ForwardedHeaders:KnownProxies: «{text}» не является IP-адресом (пример: 10.0.0.5).");
            }

            proxies.Add(address);
        }

        var networks = new List<IPNetwork>();
        foreach (var text in KnownNetworks)
        {
            if (!IPNetwork.TryParse(text?.Trim() ?? string.Empty, out var network))
            {
                throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks: «{text}» не является сетью в виде CIDR (пример: 10.0.0.0/24).");
            }

            networks.Add(network);
        }

        return (proxies, networks);
    }
}

/// <summary>Итог настройки: принимаются ли заголовки прокси и от кого (для middleware и записи в журнал).</summary>
public sealed record ForwardedHeadersPlan(bool Enabled, bool TrustLoopback, IReadOnlyList<IPAddress> Proxies, IReadOnlyList<IPNetwork> Networks);

public static class ForwardedHeadersSetup
{
    /// <summary>
    /// В Development доверяем прокси на этой же машине (loopback: dev-сервер Vite) плюс то, что задано в настройках. Вне Development только заданное:
    /// без настроенных прокси заголовки не принимаются совсем.
    /// </summary>
    public static ForwardedHeadersPlan Plan(IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        var (proxies, networks) = settings.Parse();
        var loopback = environment.IsDevelopment();
        return new ForwardedHeadersPlan(loopback || proxies.Count > 0 || networks.Count > 0, loopback, proxies, networks);
    }

    /// <summary>
    /// Настройки читаются лениво, при построении хоста, а не при регистрации: так в них попадают и переменные окружения, и значения, добавленные поверх (в тестах).
    /// Неверные настройки останавливают запуск: план строится в <see cref="UseConfiguredForwardedHeaders"/>, то есть до обработки первого запроса.
    /// </summary>
    public static IServiceCollection AddConfiguredForwardedHeaders(this IServiceCollection services)
    {
        services.AddSingleton(sp => Plan(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>()));
        services.AddOptions<ForwardedHeadersOptions>().Configure<ForwardedHeadersPlan, IConfiguration>((options, plan, configuration) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = configuration.GetSection(ForwardedHeadersSettings.SectionName).Get<ForwardedHeadersSettings>()?.ForwardLimit ?? 1;
            // По умолчанию ASP.NET доверяет loopback: убираем, чтобы список был ровно тем, что описано в настройках
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            if (plan.TrustLoopback)
            {
                options.KnownProxies.Add(IPAddress.Loopback);
                options.KnownProxies.Add(IPAddress.IPv6Loopback);
                options.KnownIPNetworks.Add(IPNetwork.Parse("127.0.0.0/8"));
            }

            foreach (var proxy in plan.Proxies)
            {
                options.KnownProxies.Add(proxy);
            }

            foreach (var network in plan.Networks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });
        return services;
    }

    /// <summary>
    /// Подключается <b>первым</b> в цепочке: до обработчика ошибок, CORS, аутентификации, лимитов и всего, что читает <c>Connection.RemoteIpAddress</c>, чтобы они увидели
    /// адрес клиента, а не прокси. Вне Development без настроенных прокси не подключается совсем (и пишет предупреждение: за прокси все пользователи слились бы в один адрес).
    /// </summary>
    public static WebApplication UseConfiguredForwardedHeaders(this WebApplication app)
    {
        var plan = app.Services.GetRequiredService<ForwardedHeadersPlan>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SkillSwap.ForwardedHeaders");
        if (!plan.Enabled)
        {
            logger.LogWarning(
                "Заголовки X-Forwarded-For и X-Forwarded-Proto не принимаются: прокси не заданы (ForwardedHeaders:KnownProxies или KnownNetworks). " +
                "Если приложение стоит за обратным прокси, все пользователи для лимитов, сеансов и аудита будут «одним адресом»: задайте адреса прокси (docs/RELEASE-CHECKLIST.md).");
            return app;
        }

        logger.LogInformation(
            "Заголовки X-Forwarded-For и X-Forwarded-Proto принимаются только от: {Trusted}.",
            string.Join(", ", plan.Proxies.Select(p => p.ToString()).Concat(plan.Networks.Select(n => n.ToString())).Concat(plan.TrustLoopback ? ["loopback"] : [])));
        app.UseForwardedHeaders();
        return app;
    }
}
