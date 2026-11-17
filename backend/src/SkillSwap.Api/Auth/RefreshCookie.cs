namespace SkillSwap.Api.Auth;

/// <summary>
/// Cookie с refresh-токеном: HttpOnly; Secure; SameSite=Strict (раздел 10.1 ТЗ).
/// Path ограничен /api/v1/auth, чтобы браузер не слал её на остальные эндпоинты.
/// </summary>
internal static class RefreshCookie
{
    public const string Name = "refresh_token";
    private const string CookiePath = "/api/v1/auth";

    public static void Set(HttpResponse response, string token, DateTime expiresUtc)
    {
        response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc)),
            IsEssential = true,
        });
    }

    public static void Delete(HttpResponse response)
    {
        // Параметры должны совпадать с Set, иначе браузер не удалит cookie.
        response.Cookies.Delete(Name, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
        });
    }
}
