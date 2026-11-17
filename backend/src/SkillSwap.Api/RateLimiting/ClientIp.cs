using System.Net;
using System.Net.Sockets;

namespace SkillSwap.Api.RateLimiting;

/// <summary>
/// Ключ клиента для счётчиков по IP. IPv4 — адрес целиком. IPv6 — только префикс /64: подсеть /64
/// обычно выдаётся одному абоненту целиком, и без этого он обходил бы лимит, меняя адрес внутри неё.
/// </summary>
public static class ClientIp
{
    public const string Unknown = "unknown";

    public static string KeyFor(HttpContext context) => KeyFor(context.Connection.RemoteIpAddress);

    public static string KeyFor(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        // IPv4, записанный в виде IPv6 (::ffff:1.2.3.4), — это тот же IPv4-клиент.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        return $"{Convert.ToHexString(bytes[..8])}/64";
    }
}
