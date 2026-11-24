using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.Middleware;

namespace SkillSwap.Api.IntegrationTests.Auth;

/// <summary>
/// Адрес клиента за обратным прокси: <c>X-Forwarded-For</c> принимается только от известных прокси (ForwardedHeaders:KnownProxies и KnownNetworks; в Development ещё loopback).
/// От остальных заголовок игнорируется: иначе любой клиент подделал бы адрес и обошёл лимиты. Адрес, который видят сеансы, лимитер, аудит и защита входа,
/// всегда <c>Connection.RemoteIpAddress</c>, а middleware подставляет туда адрес из заголовка, только если запрос пришёл от доверенного прокси.
/// </summary>
[Collection(ApiCollection.Name)]
public class ForwardedHeadersTests
{
    private const string TrustedProxy = "192.0.2.10";
    private const string UntrustedProxy = "198.51.100.7";
    private const string RealClient = "198.51.100.77";

    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public ForwardedHeadersTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string proxyIp, string? forwardedFor, string? token = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add(TestRemoteIpStartupFilter.HeaderName, proxyIp);   // «адрес сокета»: с него пришёл запрос (прокси)
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    /// <summary>Вход через прокси и адрес, который сервер записал в сеанс (его видит человек в «Сеансах входа»).</summary>
    private static async Task<string?> SessionIpAsync(WebApplicationFactory<Program> app, string email, string proxyIp, string? forwardedFor)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        var login = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/auth/login", proxyIp, forwardedFor, body: new { email, password = AuthApi.Password }));
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;

        var list = await client.SendAsync(Request(HttpMethod.Get, "/api/v1/auth/sessions", proxyIp, forwardedFor, token));
        var current = (await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single(s => s.GetProperty("isCurrent").GetBoolean());
        return current.GetProperty("ipAddress").GetString();
    }

    private WebApplicationFactory<Program> App(string environment, params (string Key, string? Value)[] settings) => _factory.WithSettings(environment, settings);

    // ---------- доверие только известным прокси ----------

    [Fact]
    public async Task A_forged_forwarded_for_from_an_untrusted_address_is_ignored()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        using var app = App("Testing", ("ForwardedHeaders:KnownProxies:0", TrustedProxy));

        var ip = await SessionIpAsync(app, email, UntrustedProxy, forwardedFor: "1.2.3.4");

        Assert.Equal(UntrustedProxy, ip);   // подделанный адрес не принят
    }

    [Fact]
    public async Task Forwarded_for_from_a_known_proxy_is_used_as_the_client_address()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        using var app = App("Testing", ("ForwardedHeaders:KnownProxies:0", TrustedProxy));

        var ip = await SessionIpAsync(app, email, TrustedProxy, forwardedFor: RealClient);

        Assert.Equal(RealClient, ip);
    }

    [Fact]
    public async Task A_proxy_inside_a_known_network_is_trusted_too()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        using var app = App("Testing", ("ForwardedHeaders:KnownNetworks:0", "192.0.2.0/24"));

        Assert.Equal(RealClient, await SessionIpAsync(app, email, "192.0.2.55", RealClient));
        Assert.Equal(UntrustedProxy, await SessionIpAsync(app, email, UntrustedProxy, "1.2.3.4"));   // соседняя сеть не доверенная
    }

    [Fact]
    public async Task What_the_client_prepends_to_the_chain_does_not_count_only_the_entry_of_the_nearest_proxy()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        using var app = App("Testing", ("ForwardedHeaders:KnownProxies:0", TrustedProxy));

        // Клиент дописал свой адрес слева; прокси добавил справа настоящий (при ForwardLimit=1 читается только правая запись)
        var ip = await SessionIpAsync(app, email, TrustedProxy, forwardedFor: $"1.2.3.4, {RealClient}");

        Assert.Equal(RealClient, ip);
    }

    [Fact]
    public async Task A_garbage_forwarded_for_from_a_known_proxy_does_not_break_the_request()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        using var app = App("Testing", ("ForwardedHeaders:KnownProxies:0", TrustedProxy));

        var ip = await SessionIpAsync(app, email, TrustedProxy, forwardedFor: "not-an-ip");

        Assert.Equal(TrustedProxy, ip);   // непонятную запись пропускаем, остаётся адрес прокси
    }

    // ---------- без настроенных прокси ----------

    [Theory]
    [InlineData("127.0.0.1")]   // даже loopback: вне Development он не доверенный
    [InlineData("::1")]
    [InlineData(UntrustedProxy)]
    public async Task Without_configured_proxies_outside_development_the_header_is_never_accepted(string proxyIp)
    {
        var email = await _api.RegisterConfirmedUserAsync();
        using var app = App("Testing");

        var ip = await SessionIpAsync(app, email, proxyIp, forwardedFor: "1.2.3.4");

        Assert.Equal(proxyIp, ip);
    }

    [Fact]
    public async Task In_development_the_loopback_proxy_is_trusted_and_other_addresses_are_not()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        using var app = App("Development");

        Assert.Equal(RealClient, await SessionIpAsync(app, email, "127.0.0.1", RealClient));   // dev-сервер Vite на этой же машине
        Assert.Equal(RealClient, await SessionIpAsync(app, email, "::1", RealClient));
        Assert.Equal(UntrustedProxy, await SessionIpAsync(app, email, UntrustedProxy, "1.2.3.4"));   // чужой адрес и в Development не доверенный
    }

    // ---------- лимитер читает адрес после middleware ----------

    private static async Task<HttpStatusCode> FailedLoginAsync(HttpClient client, string email, string proxyIp, string? forwardedFor)
    {
        var response = await client.SendAsync(Request(HttpMethod.Post, "/api/v1/auth/login", proxyIp, forwardedFor, body: new { email, password = "wrong-pass-1" }));
        return response.StatusCode;
    }

    [Fact]
    public async Task The_rate_limiter_counts_clients_behind_a_trusted_proxy_separately()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        using var app = App("Testing", ("ForwardedHeaders:KnownProxies:0", TrustedProxy), ("RateLimiting:Enabled", "true"), ("RateLimiting:LoginPerIp:PermitLimit", "2"));
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

        // Клиент A исчерпал свои две попытки; третья отклонена
        Assert.Equal(HttpStatusCode.Unauthorized, await FailedLoginAsync(client, email, TrustedProxy, "198.51.100.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await FailedLoginAsync(client, email, TrustedProxy, "198.51.100.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await FailedLoginAsync(client, email, TrustedProxy, "198.51.100.1"));

        // Клиент B за тем же прокси не пострадал: лимит считается по адресу клиента, а не по адресу прокси
        Assert.Equal(HttpStatusCode.Unauthorized, await FailedLoginAsync(client, email, TrustedProxy, "198.51.100.2"));
    }

    [Fact]
    public async Task Behind_an_untrusted_address_a_forged_header_cannot_dodge_the_rate_limiter()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        using var app = App("Testing", ("ForwardedHeaders:KnownProxies:0", TrustedProxy), ("RateLimiting:Enabled", "true"), ("RateLimiting:LoginPerIp:PermitLimit", "2"));
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.Unauthorized, await FailedLoginAsync(client, email, UntrustedProxy, "9.9.9.1"));
        Assert.Equal(HttpStatusCode.Unauthorized, await FailedLoginAsync(client, email, UntrustedProxy, "9.9.9.2"));

        // С каждым запросом новый «адрес» в заголовке, но настоящий адрес один: лимит сработал
        Assert.Equal(HttpStatusCode.TooManyRequests, await FailedLoginAsync(client, email, UntrustedProxy, "9.9.9.3"));
    }

    // ---------- настройки ----------

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "SkillSwap";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static ForwardedHeadersPlan Plan(string environment, params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value)).Build();
        return ForwardedHeadersSetup.Plan(configuration, new Env(environment));
    }

    [Fact]
    public void Development_trusts_loopback_by_default()
    {
        var plan = Plan("Development");

        Assert.True(plan.Enabled);
        Assert.True(plan.TrustLoopback);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Other_environments_accept_nothing_until_proxies_are_configured(string environment)
    {
        var plan = Plan(environment);

        Assert.False(plan.Enabled);
        Assert.False(plan.TrustLoopback);
    }

    [Fact]
    public void Configured_proxies_enable_the_middleware_without_loopback()
    {
        var plan = Plan("Production", ("ForwardedHeaders:KnownProxies:0", "10.0.0.5"), ("ForwardedHeaders:KnownNetworks:0", "10.1.0.0/16"));

        Assert.True(plan.Enabled);
        Assert.False(plan.TrustLoopback);
        Assert.Equal([IPAddress.Parse("10.0.0.5")], plan.Proxies);
        Assert.Single(plan.Networks);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-ip", "KnownProxies")]
    [InlineData("ForwardedHeaders:KnownProxies:0", "10.0.0.0/24", "KnownProxies")]          // сеть в списке адресов
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.5", "KnownNetworks")]           // адрес без маски в списке сетей
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/33", "KnownNetworks")]
    [InlineData("ForwardedHeaders:ForwardLimit", "0", "ForwardLimit")]
    public void A_bad_setting_stops_the_start_with_a_clear_message(string key, string value, string mentioned)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Plan("Production", (key, value)));

        Assert.Contains(mentioned, ex.Message);
    }
}
