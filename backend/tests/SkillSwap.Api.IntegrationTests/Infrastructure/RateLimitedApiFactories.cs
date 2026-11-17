namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Приложение с ВКЛЮЧЁННЫМ ограничением частоты и значениями по умолчанию, то есть теми, что лежат в appsettings.json.
/// Так проверяется, что поставляемая конфигурация действительно соответствует ТЗ.
/// </summary>
public sealed class RateLimitedApiFactory : ApiFactory
{
    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        base.ConfigureSettings(settings);   // капча выключена, секрет задан
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
        base.ConfigureSettings(settings);
        settings["RateLimiting:Enabled"] = "true";
        settings["RateLimiting:LoginPerIp:PermitLimit"] = "2";
        settings["RateLimiting:RegisterPerIp:PermitLimit"] = "1";
        settings["RateLimiting:MailPerEmail:PermitLimit"] = "1";
        settings["RateLimiting:MailPerIp:PermitLimit"] = "2";
        settings["RateLimiting:AuthenticatedPerUser:PermitLimit"] = "5";
        settings["RateLimiting:ResetPasswordPerIp:PermitLimit"] = "1";
        settings["RateLimiting:ConfirmEmailPerIp:PermitLimit"] = "2";
        settings["RateLimiting:RefreshPerIp:PermitLimit"] = "3";
    }
}

/// <summary>
/// Приложение с ВКЛЮЧЁННОЙ капчей Turnstile (против заглушки Cloudflare) и включённым ограничением частоты:
/// так проверяется и сама капча, и порядок защит (лимит по IP → капча → лимит по email).
/// Таймаут проверки — 1 секунда, чтобы сбой «Cloudflare не отвечает» проверялся быстро.
/// </summary>
public sealed class CaptchaApiFactory : ApiFactory
{
    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        base.ConfigureSettings(settings);
        settings["Turnstile:Enabled"] = "true";
        settings["Turnstile:TimeoutSeconds"] = "1";
        settings["RateLimiting:Enabled"] = "true";
    }
}

/// <summary>Капча включена и разрешено передавать Cloudflare IP клиента (Turnstile:SendClientIp=true).</summary>
public sealed class CaptchaSendIpApiFactory : ApiFactory
{
    protected override void ConfigureSettings(IDictionary<string, string?> settings)
    {
        base.ConfigureSettings(settings);
        settings["Turnstile:Enabled"] = "true";
        settings["Turnstile:SendClientIp"] = "true";
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