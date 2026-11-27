using System.Net;

namespace SkillSwap.Api.DevTools;

/// <summary>
/// Маршруты разработки (<c>/dev/*</c>: вход без пароля, сиды, коды 2FA сотрудников) существуют только в Development и отвечают только запросам
/// с этого же компьютера. Адрес берётся после разбора X-Forwarded-For: прокси Vite (loopback) передаёт адрес браузера, поэтому страница,
/// открытая с телефона или по адресу компьютера в сети, получает 404, как будто маршрута нет.
/// </summary>
public static class DevToolsAccess
{
    public static IApplicationBuilder UseDevToolsLoopbackOnly(this IApplicationBuilder app) =>
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/dev"),
            branch => branch.Use(async (context, next) =>
            {
                if (!IsLocal(context.Connection.RemoteIpAddress))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await next(context);
            }));

    public static bool IsLocal(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
