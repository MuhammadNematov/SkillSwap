using SkillSwap.Application.Auth;
using UAParser;

namespace SkillSwap.Infrastructure.Auth;

/// <summary>
/// Разбор User-Agent (библиотека UAParser) в скромное название устройства: «Браузер · система» («Safari · macOS», «Chrome · Android», «Safari · iPhone»).
/// Только то, что человек и так знает о своём устройстве: ни города, ни провайдера, ни модели. Не определилось, значит <c>null</c>.
/// </summary>
public sealed class UaDeviceInfoParser : IDeviceInfoParser
{
    // Правила разбора грузятся один раз на процесс: это разбор встроенного YAML, он заметно дороже самого запроса.
    private static readonly Lazy<Parser> Library = new(() => Parser.GetDefault());

    public DeviceDescription Describe(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return DeviceDescription.Unknown;
        }

        UAParser.ClientInfo client;
        try
        {
            client = Library.Value.Parse(userAgent);
        }
        catch (Exception)
        {
            return DeviceDescription.Unknown;   // странная строка не должна ломать список сеансов
        }

        if (client.Device.IsSpider)
        {
            return DeviceDescription.Unknown;
        }

        var (system, type) = System(client, userAgent);
        if (system is null)
        {
            return DeviceDescription.Unknown;   // не браузер (curl, приложение): честнее написать «неизвестное устройство», чем название программы
        }

        var browser = Browser(client.UA.Family);
        return new DeviceDescription(browser is null ? system : $"{browser} · {system}", type);
    }

    /// <summary>Система (для телефонов и планшетов Apple сама модель: «iPhone», «iPad») и вид устройства.</summary>
    private static (string? Name, DeviceType Type) System(UAParser.ClientInfo client, string userAgent)
    {
        var os = client.OS.Family;
        var device = client.Device.Family;
        if (os == "iOS" || device is "iPhone" or "iPad" or "iPod")
        {
            return device switch
            {
                "iPad" => ("iPad", DeviceType.Tablet),
                "iPhone" => ("iPhone", DeviceType.Phone),
                "iPod" => ("iPod", DeviceType.Phone),
                _ => ("iOS", DeviceType.Phone),
            };
        }

        if (os == "Android")
        {
            // У Chrome на телефоне в User-Agent есть слово Mobile, на планшете его нет
            return ("Android", userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ? DeviceType.Phone : DeviceType.Tablet);
        }

        if (os is "Mac OS X" or "macOS")
        {
            return ("macOS", DeviceType.Desktop);
        }

        if (os.StartsWith("Windows", StringComparison.Ordinal))
        {
            return os.Contains("Phone", StringComparison.Ordinal) ? ("Windows", DeviceType.Phone) : ("Windows", DeviceType.Desktop);
        }

        if (os == "Chrome OS")
        {
            return ("ChromeOS", DeviceType.Desktop);
        }

        if (os is "Linux" or "Ubuntu" or "Debian" or "Fedora" or "Arch Linux" or "Mint" or "Red Hat" or "CentOS" or "SUSE" or "Gentoo" or "FreeBSD")
        {
            return ("Linux", DeviceType.Desktop);
        }

        return (null, DeviceType.Unknown);
    }

    /// <summary>«Chrome Mobile iOS» → «Chrome», «Mobile Safari» → «Safari», «Firefox Mobile» → «Firefox»; «Other» → <c>null</c>.</summary>
    private static string? Browser(string family)
    {
        var name = family;
        foreach (var noise in new[] { "Mobile Safari UI/WKWebView", "Mobile Safari", "Chrome Mobile WebView" })
        {
            if (name.StartsWith(noise, StringComparison.Ordinal))
            {
                name = noise.Contains("Safari", StringComparison.Ordinal) ? "Safari" : "Chrome";
            }
        }

        name = name.Replace(" Mobile", "", StringComparison.Ordinal).Replace(" iOS", "", StringComparison.Ordinal).Replace(" WebView", "", StringComparison.Ordinal).Trim();
        return name is "" or "Other" ? null : name;
    }
}
