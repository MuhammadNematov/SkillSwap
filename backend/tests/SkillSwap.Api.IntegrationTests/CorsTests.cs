using System.Net;
using System.Net.Http.Headers;
using SkillSwap.Api.IntegrationTests.Infrastructure;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>
/// CORS для фронтенда (<c>Frontend:BaseUrl</c>, локально http://localhost:5173): REST и SignalR (согласование соединения и предварительные запросы) отвечают
/// этому адресу с разрешением cookie (credentials), чужому адресу — без разрешения. Нужно для refresh-cookie и для хабов чата и уведомлений.
/// </summary>
[Collection(ApiCollection.Name)]
public class CorsTests
{
    private const string Dev = "http://localhost:5173";

    private readonly ApiFactory _factory;

    public CorsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient DevClient() =>
        _factory.WithSettings("Testing", ("Frontend:BaseUrl", Dev)).CreateClient();

    private static HttpRequestMessage Preflight(string url, string origin, string method, string headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return request;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    // ---------- REST ----------

    [Theory]
    [InlineData("/api/v1/auth/login", "POST", "content-type")]
    [InlineData("/api/v1/auth/refresh", "POST", "content-type")]
    [InlineData("/api/v1/me", "GET", "authorization,accept-language")]
    [InlineData("/api/v1/exchanges/00000000-0000-0000-0000-000000000000/abort", "POST", "authorization,content-type")]
    [InlineData("/api/v1/me/avatar", "PUT", "authorization")]
    [InlineData("/api/v1/users/00000000-0000-0000-0000-000000000000/block", "DELETE", "authorization")]
    public async Task A_preflight_from_the_frontend_is_allowed_with_credentials_for_every_method_and_header_it_uses(string url, string method, string headers)
    {
        var response = await DevClient().SendAsync(Preflight(url, Dev, method, headers));

        Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, $"{(int)response.StatusCode}");
        Assert.Equal(Dev, Header(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(response, "Access-Control-Allow-Credentials"));
        Assert.Contains(method, Header(response, "Access-Control-Allow-Methods") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        foreach (var header in headers.Split(','))
        {
            Assert.Contains(header, Header(response, "Access-Control-Allow-Headers") ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task An_ordinary_response_to_the_frontend_carries_the_origin_credentials_and_exposes_retry_after()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/skill-categories");
        request.Headers.Add("Origin", Dev);

        var response = await DevClient().SendAsync(request);

        Assert.Equal(Dev, Header(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(response, "Access-Control-Allow-Credentials"));
        Assert.Contains("Retry-After", Header(response, "Access-Control-Expose-Headers") ?? string.Empty);
        Assert.NotEqual("*", Header(response, "Access-Control-Allow-Origin"));   // с credentials звёздочка недопустима
    }

    [Fact]
    public async Task An_error_response_is_readable_by_the_frontend_too()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");   // без токена: 401
        request.Headers.Add("Origin", Dev);

        var response = await DevClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(Dev, Header(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(response, "Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("http://localhost:5174")]
    [InlineData("http://localhost")]
    [InlineData("https://localhost:5173")]
    [InlineData("http://evil.example")]
    [InlineData("null")]
    public async Task Any_other_origin_gets_no_permission(string origin)
    {
        var preflight = await DevClient().SendAsync(Preflight("/api/v1/auth/login", origin, "POST", "content-type"));

        Assert.Null(Header(preflight, "Access-Control-Allow-Origin"));
        Assert.Null(Header(preflight, "Access-Control-Allow-Credentials"));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/skill-categories");
        request.Headers.Add("Origin", origin);
        var ordinary = await DevClient().SendAsync(request);
        Assert.Null(Header(ordinary, "Access-Control-Allow-Origin"));
    }

    // ---------- SignalR ----------

    [Theory]
    [InlineData("/hubs/chat/negotiate?negotiateVersion=1")]
    [InlineData("/hubs/notifications/negotiate?negotiateVersion=1")]
    public async Task The_hub_preflight_allows_the_headers_the_signalr_client_sends(string url)
    {
        var response = await DevClient().SendAsync(
            Preflight(url, Dev, "POST", "authorization,content-type,x-requested-with,x-signalr-user-agent"));

        Assert.True(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, $"{(int)response.StatusCode}");
        Assert.Equal(Dev, Header(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(response, "Access-Control-Allow-Credentials"));
        var allowed = Header(response, "Access-Control-Allow-Headers") ?? string.Empty;
        Assert.Contains("x-signalr-user-agent", allowed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authorization", allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/hubs/chat/negotiate?negotiateVersion=1")]
    [InlineData("/hubs/notifications/negotiate?negotiateVersion=1")]
    public async Task The_hub_negotiation_with_a_token_answers_the_frontend_and_without_one_still_carries_cors_headers(string url)
    {
        var client = DevClient();

        var anonymous = new HttpRequestMessage(HttpMethod.Post, url);
        anonymous.Headers.Add("Origin", Dev);
        var refused = await client.SendAsync(anonymous);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(Dev, Header(refused, "Access-Control-Allow-Origin"));   // фронтенд видит 401, а не «CORS-ошибку»
        Assert.Equal("true", Header(refused, "Access-Control-Allow-Credentials"));

        var api = new AuthApi(_factory);
        var email = await api.RegisterConfirmedUserAsync();
        var session = await api.LoginSessionAsync(email);
        var authorized = new HttpRequestMessage(HttpMethod.Post, url);
        authorized.Headers.Add("Origin", Dev);
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var accepted = await client.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(Dev, Header(accepted, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(accepted, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task A_web_socket_connection_can_authenticate_with_the_token_in_the_query_string()
    {
        var api = new AuthApi(_factory);
        var email = await api.RegisterConfirmedUserAsync();
        var session = await api.LoginSessionAsync(email);
        var client = DevClient();

        // Браузерный WebSocket не умеет ставить заголовок Authorization: токен идёт параметром access_token (раздел «Чат» в BACKEND-DEV.md).
        var withQuery = new HttpRequestMessage(HttpMethod.Post, $"/hubs/chat/negotiate?negotiateVersion=1&access_token={session.AccessToken}");
        withQuery.Headers.Add("Origin", Dev);
        var response = await client.SendAsync(withQuery);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_allowed_origin_follows_the_frontend_setting_and_a_trailing_slash_is_ignored()
    {
        var client = _factory.WithSettings("Testing", ("Frontend:BaseUrl", Dev + "/")).CreateClient();

        var response = await client.SendAsync(Preflight("/api/v1/auth/login", Dev, "POST", "content-type"));

        Assert.Equal(Dev, Header(response, "Access-Control-Allow-Origin"));
    }
}
