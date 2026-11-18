namespace SkillSwap.Application.Auth;

/// <summary>
/// Токены из ссылок в письмах (подтверждение email, сброс пароля). Identity выдаёт обычный Base64 с символами «+», «/», «=»,
/// которые ломаются в адресе. В ссылку токен кладётся в виде Base64Url («-», «_», без «=»): фронтенд берёт его из адреса
/// как есть и отправляет на сервер без перекодирования. Сервер принимает оба вида.
/// </summary>
public static class UrlSafeToken
{
    /// <summary>Токен Identity → Base64Url (для ссылки).</summary>
    public static string Encode(string identityToken) =>
        identityToken.TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Base64Url → токен Identity. Обычный Base64 проходит без изменений. Никогда не бросает исключение:
    /// мусор остаётся мусором, и Identity отвергнет его как неверный токен.
    /// </summary>
    public static string Decode(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return token ?? string.Empty;
        }

        var standard = token.Replace('-', '+').Replace('_', '/');
        var padding = (4 - standard.Length % 4) % 4;
        return padding == 0 ? standard : standard + new string('=', padding);
    }
}
