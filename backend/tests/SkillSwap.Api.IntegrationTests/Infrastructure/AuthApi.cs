using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>Тонкая обёртка над HttpClient: эндпоинты Auth и ручная работа с refresh-cookie.</summary>
public sealed partial class AuthApi
{
    public const string Password = "abc12345";
    private const string Base = "/api/v1";

    private readonly ApiFactory _factory;

    public AuthApi(ApiFactory factory)
    {
        _factory = factory;
        // Cookie с флагом Secure не уходит по http, поэтому cookie передаём вручную, как это делает браузер по https.
        Client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            AllowAutoRedirect = false,
        });
    }

    public HttpClient Client { get; }

    /// <summary>SHA-256 в hex (верхний регистр): так сервер хранит хеш refresh-токена. Считаем независимо от кода сервера.</summary>
    public static string Sha256Hex(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    public static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    public Task<HttpResponseMessage> RegisterAsync(string email, string? acceptLanguage = null, bool? acceptTerms = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/auth/register")
        {
            Content = JsonContent.Create(new
            {
                email,
                password = Password,
                name = "Test User",
                dateOfBirth = "2000-01-01",
                acceptTerms,
                acceptChatModeration = true,
            }),
        };
        if (acceptLanguage is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        }

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> ConfirmEmailAsync(string userId, string token) =>
        Client.PostAsJsonAsync($"{Base}/auth/confirm-email", new { userId, token });

    /// <summary>Регистрирует пользователя и подтверждает email по ссылке из (перехваченного) письма.</summary>
    public async Task<string> RegisterConfirmedUserAsync()
    {
        var email = NewEmail();
        (await RegisterAsync(email)).EnsureSuccessStatusCode();
        var (userId, token) = _factory.Mail.LastConfirmationLink(email);
        (await ConfirmEmailAsync(userId, token)).EnsureSuccessStatusCode();
        return email;
    }

    public Task<HttpResponseMessage> LoginAsync(string email, string password = Password) =>
        Client.PostAsJsonAsync($"{Base}/auth/login", new { email, password });

    // ---------- 2FA ----------

    public Task<HttpResponseMessage> TwoFactorSetupAsync(string? accessToken, string? twoFactorToken = null) =>
        PostAsync("/auth/2fa/setup", new { twoFactorToken }, accessToken);

    public Task<HttpResponseMessage> TwoFactorVerifyAsync(string? accessToken, string code, string? twoFactorToken = null) =>
        PostAsync("/auth/2fa/verify", new { code, twoFactorToken }, accessToken);

    public Task<HttpResponseMessage> LoginTwoFactorAsync(string twoFactorToken, string code, bool isRecoveryCode = false) =>
        PostAsync("/auth/login/2fa", new { twoFactorToken, code, isRecoveryCode }, null);

    public Task<HttpResponseMessage> TwoFactorDisableAsync(string accessToken, string password, string code) =>
        PostAsync("/auth/2fa/disable", new { password, code }, accessToken);

    /// <summary>Полная настройка 2FA обычным пользователем; возвращает ключ аутентификатора и резервные коды.</summary>
    public async Task<(string Key, IReadOnlyList<string> RecoveryCodes)> EnableTwoFactorAsync(string accessToken)
    {
        var setup = await TwoFactorSetupAsync(accessToken);
        setup.EnsureSuccessStatusCode();
        var key = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;

        var verify = await TwoFactorVerifyAsync(accessToken, Totp.Now(key));
        verify.EnsureSuccessStatusCode();
        var codes = (await verify.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes")
            .EnumerateArray().Select(c => c.GetString()!).ToList();
        return (key, codes);
    }

    /// <summary>Из ответа /auth/login достаёт промежуточный токен 2FA.</summary>
    public static async Task<string> ReadTwoFactorTokenAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("twoFactorToken").GetString()!;

    public static async Task<bool> ReadFlagAsync(HttpResponseMessage response, string name)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body, string? accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}{path}") { Content = JsonContent.Create(body) };
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> RefreshAsync(string? refreshToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/auth/refresh");
        if (refreshToken is not null)
        {
            request.Headers.Add("Cookie", $"refresh_token={refreshToken}");
        }

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> LogoutAsync(string refreshToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/auth/logout");
        request.Headers.Add("Cookie", $"refresh_token={refreshToken}");
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> MeAsync(string? accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/me");
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return Client.SendAsync(request);
    }

    /// <summary>Вход подтверждённым пользователем: access-токен из тела и refresh-токен из cookie.</summary>
    public async Task<(string AccessToken, string RefreshToken)> LoginTokensAsync(string email)
    {
        var response = await LoginAsync(email);
        response.EnsureSuccessStatusCode();
        return (await ReadAccessTokenAsync(response), RefreshTokenFrom(response)!);
    }

    public static async Task<string> ReadAccessTokenAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("accessToken").GetString()!;
    }

    public static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>Значение refresh-токена из заголовка Set-Cookie (или null, если cookie не выставлена).</summary>
    public static string? RefreshTokenFrom(HttpResponseMessage response)
    {
        var setCookie = SetCookieHeader(response);
        var match = setCookie is null ? null : RefreshCookieRegex().Match(setCookie);
        return match is { Success: true } ? match.Groups[1].Value : null;
    }

    public static string? SetCookieHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("refresh_token=", StringComparison.Ordinal))
            : null;

    [GeneratedRegex("^refresh_token=([^;]*)")]
    private static partial Regex RefreshCookieRegex();
}
