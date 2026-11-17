using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkillSwap.Api.IntegrationTests.Infrastructure;

namespace SkillSwap.Api.IntegrationTests.LiveKit;

/// <summary>
/// Dev-инструменты LiveKit (выдача токена и тестовая страница): существуют ТОЛЬКО в среде Development.
/// В остальных средах маршрутов нет вообще (404), а не «закрыты проверкой».
/// </summary>
[Collection(ApiCollection.Name)]
public class LiveKitDevToolsTests
{
    private readonly ApiFactory _factory;

    public LiveKitDevToolsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private static readonly WebApplicationFactoryClientOptions NoRedirect = new() { AllowAutoRedirect = false };

    private HttpClient DevClient(out WebApplicationFactory<Program> app, params (string Key, string? Value)[] settings)
    {
        app = _factory.WithSettings("Development", settings);
        return app.CreateClient(NoRedirect);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonWebToken> ValidateAsync(string token)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = ApiFactory.LiveKitApiKey,
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.LiveKitApiSecret)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = false,
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        return (JsonWebToken)result.SecurityToken;
    }

    // ---------- только Development ----------

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Outside_development_the_dev_tools_do_not_exist(string environment)
    {
        using var app = _factory.WithSettings(environment, ("Turnstile:SecretKey", "0x4AAAAAAAxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"));
        var client = app.CreateClient(NoRedirect);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/livekit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/livekit/token")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/livekit/token?room=dev-x&identity=a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/livekit/")).StatusCode);
    }

    [Fact]
    public async Task In_the_default_test_environment_the_dev_tools_are_absent_too()
    {
        var client = _factory.CreateClient(NoRedirect);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/livekit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/dev/livekit/token")).StatusCode);
    }

    // ---------- выдача токена ----------

    [Fact]
    public async Task The_token_endpoint_issues_a_short_token_for_the_default_test_room()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var response = await client.GetAsync("/dev/livekit/token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal("dev-test-room", json.GetProperty("room").GetString());
        Assert.StartsWith("dev-", json.GetProperty("identity").GetString());
        Assert.Equal(ApiFactory.LiveKitUrl, json.GetProperty("url").GetString());   // адрес берётся из настроек
        Assert.Equal(_factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(5), json.GetProperty("expiresAt").GetDateTime());
    }

    [Fact]
    public async Task The_issued_token_is_a_real_signed_livekit_token_for_that_room_and_identity()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var json = await JsonAsync(await client.GetAsync("/dev/livekit/token?room=dev-lesson-1&identity=alice-1&name=Alice"));

        var jwt = await ValidateAsync(json.GetProperty("token").GetString()!);
        Assert.Equal("alice-1", jwt.Subject);
        Assert.Equal("Alice", jwt.GetPayloadValue<string>("name"));
        var video = jwt.GetPayloadValue<JsonElement>("video");
        Assert.Equal("dev-lesson-1", video.GetProperty("room").GetString());
        Assert.True(video.GetProperty("roomJoin").GetBoolean());
        Assert.Equal(TimeSpan.FromMinutes(5), jwt.ValidTo - jwt.ValidFrom);
        Assert.Equal("dev-lesson-1", json.GetProperty("room").GetString());
        Assert.Equal("alice-1", json.GetProperty("identity").GetString());
    }

    [Fact]
    public async Task The_token_lifetime_follows_the_configuration()
    {
        var client = DevClient(out var app, ("LiveKit:TokenTtlSeconds", "90"));
        using var _ = app;

        var json = await JsonAsync(await client.GetAsync("/dev/livekit/token"));

        Assert.Equal(_factory.Clock.GetUtcNow().UtcDateTime.AddSeconds(90), json.GetProperty("expiresAt").GetDateTime());
    }

    [Fact]
    public async Task Without_an_identity_every_call_gets_a_fresh_one_so_two_tabs_do_not_kick_each_other_out()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var a = (await JsonAsync(await client.GetAsync("/dev/livekit/token"))).GetProperty("identity").GetString();
        var b = (await JsonAsync(await client.GetAsync("/dev/livekit/token"))).GetProperty("identity").GetString();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Surrounding_spaces_are_trimmed_and_an_empty_name_falls_back_to_the_identity()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var json = await JsonAsync(await client.GetAsync("/dev/livekit/token?room=%20dev-x%20&identity=%20bob-1%20&name=%20"));

        Assert.Equal("dev-x", json.GetProperty("room").GetString());
        Assert.Equal("bob-1", json.GetProperty("identity").GetString());
        Assert.Equal("bob-1", (await ValidateAsync(json.GetProperty("token").GetString()!)).GetPayloadValue<string>("name"));
    }

    [Fact]
    public async Task The_token_is_never_cached()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var response = await client.GetAsync("/dev/livekit/token");

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task The_token_endpoint_works_without_a_login_it_is_a_dev_only_convenience()
    {
        var client = DevClient(out var app);
        using var _ = app;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dev/livekit/token")).StatusCode);   // без Authorization
    }

    [Fact]
    public async Task Only_get_is_allowed_on_the_token_endpoint()
    {
        var client = DevClient(out var app);
        using var _ = app;

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/dev/livekit/token", null)).StatusCode);
    }

    // ---------- проверка входных данных ----------

    [Theory]
    [InlineData("test-room", "invalid_room")]                  // без префикса dev-
    [InlineData("session-5f3c", "invalid_room")]               // настоящие комнаты уроков недоступны прототипу
    [InlineData("Session-5f3c", "invalid_room")]
    [InlineData("dev-Upper", "invalid_room")]                  // заглавные
    [InlineData("dev-with space", "invalid_room")]
    [InlineData("dev-under_score", "invalid_room")]
    [InlineData("dev-../etc", "invalid_room")]
    [InlineData("dev-", "invalid_room")]                       // пустой хвост
    [InlineData("dev-%3Cscript%3E", "invalid_room")]
    [InlineData("dev-ünïcode", "invalid_room")]
    public async Task A_room_name_outside_the_test_pattern_is_refused(string room, string code)
    {
        var client = DevClient(out var app);
        using var _ = app;

        var response = await client.GetAsync("/dev/livekit/token?room=" + room);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_room_name_of_the_maximum_length_passes_and_one_character_more_is_refused()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var longest = "dev-" + new string('a', 59);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dev/livekit/token?room=" + longest)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/dev/livekit/token?room=" + longest + "a")).StatusCode);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("a@b")]
    [InlineData("слово")]
    [InlineData("a/b")]
    [InlineData("%3Cb%3E")]
    public async Task An_identity_outside_the_pattern_is_refused(string identity)
    {
        var client = DevClient(out var app);
        using var _ = app;

        var response = await client.GetAsync("/dev/livekit/token?identity=" + identity);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_identity", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_identity_of_the_maximum_length_passes_and_one_character_more_is_refused()
    {
        var client = DevClient(out var app);
        using var _ = app;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dev/livekit/token?identity=" + new string('a', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/dev/livekit/token?identity=" + new string('a', 65))).StatusCode);
    }

    [Fact]
    public async Task A_name_longer_than_64_characters_is_refused_but_non_latin_names_are_fine()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var tooLong = await client.GetAsync("/dev/livekit/token?name=" + new string('x', 65));
        var cyrillic = await client.GetAsync("/dev/livekit/token?name=" + Uri.EscapeDataString("Алиса из тестовой комнаты"));

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("invalid_name", (await JsonAsync(tooLong)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, cyrillic.StatusCode);
    }

    // ---------- LiveKit не настроен ----------

    [Fact]
    public async Task Without_a_secret_the_token_endpoint_explains_what_to_set_instead_of_failing()
    {
        var client = DevClient(out var app, ("LiveKit:ApiSecret", ""));
        using var _ = app;

        var response = await client.GetAsync("/dev/livekit/token");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await JsonAsync(response);
        Assert.Equal("livekit_not_configured", json.GetProperty("code").GetString());
        Assert.Contains("LiveKit:ApiSecret", json.GetProperty("title").GetString());
    }

    // ---------- тестовая страница ----------

    [Fact]
    public async Task The_test_page_is_served_as_html_and_never_cached()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var response = await client.GetAsync("/dev/livekit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task The_page_loads_a_pinned_livekit_client_with_an_integrity_hash()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var html = await client.GetStringAsync("/dev/livekit");

        Assert.Contains("https://cdn.jsdelivr.net/npm/livekit-client@2.23.0/dist/livekit-client.umd.min.js", html);   // версия закреплена
        Assert.Contains("integrity=\"sha384-5Eup/y0O0HaGMTnRv9dQddV/+npZKkzbmhjrFfyJsiMgphEG4bpxscOotD6/NPPj\"", html);
        Assert.Contains("crossorigin=\"anonymous\"", html);
        Assert.DoesNotContain("livekit-client@latest", html);
        Assert.DoesNotContain("@2\"", html);   // не «плавающая» мажорная версия
    }

    [Fact]
    public async Task The_page_has_what_is_needed_to_try_a_call_in_two_tabs()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var html = await client.GetStringAsync("/dev/livekit");

        foreach (var marker in new[] { "id=\"room\"", "id=\"name\"", "id=\"join\"", "id=\"leave\"", "id=\"ping\"", "id=\"cam\"", "id=\"mic\"",
                                       "id=\"participants\"", "id=\"log\"", "/dev/livekit/token", "ConnectionStateChanged", "TrackSubscribed", "DataReceived" })
        {
            Assert.Contains(marker, html);
        }
    }

    [Fact]
    public async Task The_page_contains_no_secrets_and_no_token_and_gets_everything_from_the_token_endpoint()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var html = await client.GetStringAsync("/dev/livekit");

        Assert.DoesNotContain(ApiFactory.LiveKitApiSecret, html);
        Assert.DoesNotContain("eyJ", html);                      // в странице нет ни одного JWT
        Assert.DoesNotContain("ApiSecret", html);
        Assert.DoesNotContain("ws://livekit.test", html);        // адрес сервера приходит из настроек, а не вшит
    }

    [Fact]
    public async Task The_page_writes_other_peoples_names_as_text_not_as_markup()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var html = await client.GetStringAsync("/dev/livekit");

        Assert.DoesNotContain("innerHTML", html);   // чужое имя в соседней вкладке не должно стать разметкой
        Assert.Contains("textContent", html);
    }

    [Fact]
    public async Task The_page_supports_automation_through_the_url()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var html = await client.GetStringAsync("/dev/livekit");

        Assert.Contains("params.get('auto')", html);
        Assert.Contains("dataset.state", html);   // состояние выводится в атрибут для автоматических проверок
    }

    // ---------- договор с фронтендом ----------

    [Fact]
    public async Task The_openapi_document_lists_the_webhook_but_not_the_dev_tools()
    {
        var client = DevClient(out var app);
        using var _ = app;

        var openApi = await client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("/api/v1/webhooks/livekit", openApi);
        Assert.DoesNotContain("/dev/livekit", openApi);   // инструменты разработчика не часть договора
    }
}
