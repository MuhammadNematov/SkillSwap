namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Приложение с ВКЛЮЧЁННЫМ ограничением частоты и значениями по умолчанию, то есть теми, что лежат в appsettings.json.
/// Так проверяется, что поставляемая конфигурация действительно соответствует ТЗ.
/// </summary>
public sealed class RateLimitedApiFactory : ApiFactory
{
    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        settings["RateLimiting:Enabled"] = "true";
    }
}

/// <summary>
/// Приложение с другими, заниженными лимитами: доказывает, что значения берутся из конфигурации, а не зашиты в код.
/// </summary>
public sealed class CustomLimitsApiFactory : ApiFactory
{
    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        settings["RateLimiting:Enabled"] = "true";
        settings["RateLimiting:LoginPerIp:PermitLimit"] = "2";
        settings["RateLimiting:RegisterPerIp:PermitLimit"] = "1";
        settings["RateLimiting:MailPerEmail:PermitLimit"] = "1";
        settings["RateLimiting:MailPerIp:PermitLimit"] = "2";
        settings["RateLimiting:AuthenticatedPerUser:PermitLimit"] = "5";
    }
}

/// <summary>Уникальные адреса клиентов: каждый тест лимитов «приходит» со своего IP и не мешает остальным.</summary>
public static class TestIps
{
    private static int _next;

    public static string Next()
    {
        var n = Interlocked.Increment(ref _next);
        return $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}";
    }

    /// <summary>Уникальный префикс IPv6 /64 (первые четыре группы). К нему дописывают «::1», «::ffff:1» и т. п.</summary>
    public static string NextIpv6Prefix()
    {
        var n = Interlocked.Increment(ref _next);
        return $"2001:db8:{(n >> 16) & 0xffff:x}:{n & 0xffff:x}";
    }
}