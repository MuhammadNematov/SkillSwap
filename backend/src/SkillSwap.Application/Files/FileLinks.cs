using SkillSwap.Application.Options;

namespace SkillSwap.Application.Files;

/// <summary>
/// Ссылки на файлы для мест, где нет страницы сайта, от которой браузер взял бы адрес (письма). В режиме <c>Storage:RelativeUrls</c> (только разработка)
/// хранилище отдаёт путь без адреса, и в письме он ничего бы не открыл: дописываем адрес сайта (<c>Frontend:BaseUrl</c>), запрос дойдёт до хранилища через тот же прокси.
/// Абсолютная ссылка (обычный режим, <c>Storage:PublicEndpoint</c>) остаётся как есть.
/// </summary>
public static class FileLinks
{
    public static string Absolute(string link, FrontendOptions frontend) =>
        link.StartsWith('/') && !link.StartsWith("//", StringComparison.Ordinal) ? frontend.BaseUrl.TrimEnd('/') + link : link;
}
