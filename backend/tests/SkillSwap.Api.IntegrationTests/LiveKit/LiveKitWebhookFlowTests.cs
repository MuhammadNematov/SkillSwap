using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Api.IntegrationTests.LiveKit;

/// <summary>
/// Приём webhook LiveKit на настоящем приложении и PostgreSQL: проверка подписи, запись в LiveKitWebhookEvents
/// с уникальным EventId, идемпотентность (в том числе при гонке), отказ при любой неопределённости.
/// </summary>
[Collection(ApiCollection.Name)]
public class LiveKitWebhookFlowTests
{
    private const string Url = "/api/v1/webhooks/livekit";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public LiveKitWebhookFlowTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private string Sign(string body, LiveKitSigner.Options? options = null) => LiveKitSigner.Sign(body, Now, options);

    private static HttpRequestMessage Request(string body, string? authorization, string contentType = "application/webhook+json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(body, new UTF8Encoding(false), contentType),
        };
        if (authorization is not null)
        {
            // Как у LiveKit: голый JWT в Authorization, без схемы «Bearer».
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return request;
    }

    private Task<HttpResponseMessage> PostSignedAsync(string body, LiveKitSigner.Options? options = null, HttpClient? client = null) =>
        (client ?? _client).SendAsync(Request(body, Sign(body, options)));

    private Task<List<LiveKitWebhookEvent>> RowsAsync(string room) =>
        _factory.QueryAsync(db => db.Set<LiveKitWebhookEvent>().Where(e => e.RoomName == room).OrderBy(e => e.OccurredAt).ToListAsync());

    private Task<int> CountByIdAsync(string eventId) =>
        _factory.QueryAsync(db => db.Set<LiveKitWebhookEvent>().CountAsync(e => e.EventId == eventId));

    // ---------- приём и запись ----------

    [Fact]
    public async Task A_correctly_signed_event_is_accepted_and_stored_with_its_fields()
    {
        var room = LiveKitBodies.NewRoom();
        var id = LiveKitBodies.NewEventId();

        var response = await PostSignedAsync(LiveKitBodies.ParticipantJoined(id, room, "alice-1", createdAt: 1794940518));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        var row = Assert.Single(await RowsAsync(room));
        Assert.Equal(id, row.EventId);
        Assert.Equal("participant_joined", row.Type);
        Assert.Equal(room, row.RoomName);
        Assert.Equal("alice-1", row.ParticipantIdentity);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794940518).UtcDateTime, row.OccurredAt);
        Assert.Null(row.ProcessedAt);   // обработка в посещаемость — этап 5
        Assert.NotEqual(Guid.Empty, row.Id);
        Assert.True(row.CreatedAt > _factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(-5));
        Assert.Equal(row.CreatedAt, row.UpdatedAt);
    }

    [Fact]
    public async Task A_whole_call_lifecycle_is_stored_event_by_event()
    {
        var room = LiveKitBodies.NewRoom();
        var events = new[]
        {
            LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), room, 1794940501),
            LiveKitBodies.ParticipantJoined(LiveKitBodies.NewEventId(), room, "alice-1", createdAt: 1794940502),
            LiveKitBodies.ParticipantJoined(LiveKitBodies.NewEventId(), room, "bob-1", createdAt: 1794940503),
            LiveKitBodies.ParticipantLeft(LiveKitBodies.NewEventId(), room, "bob-1", 1794940530),
            LiveKitBodies.ParticipantLeft(LiveKitBodies.NewEventId(), room, "alice-1", 1794940531),
            LiveKitBodies.RoomFinished(LiveKitBodies.NewEventId(), room, 1794940551),
        };

        foreach (var body in events)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostSignedAsync(body)).StatusCode);
        }

        var rows = await RowsAsync(room);
        Assert.Equal(
            ["room_started", "participant_joined", "participant_joined", "participant_left", "participant_left", "room_finished"],
            rows.Select(r => r.Type));
        Assert.Equal([null, "alice-1", "bob-1", "bob-1", "alice-1", null], rows.Select(r => r.ParticipantIdentity));
    }

    [Fact]
    public async Task The_same_event_delivered_twice_is_answered_200_both_times_and_stored_once()
    {
        var room = LiveKitBodies.NewRoom();
        var body = LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), room);

        var first = await PostSignedAsync(body);
        var second = await PostSignedAsync(body);   // LiveKit повторяет доставку, если не получил 200

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(await RowsAsync(room));
    }

    [Fact]
    public async Task A_repeated_id_never_overwrites_the_first_event_even_with_different_content()
    {
        var room = LiveKitBodies.NewRoom();
        var id = LiveKitBodies.NewEventId();
        await PostSignedAsync(LiveKitBodies.RoomStarted(id, room));

        // Тот же id, но другое содержимое (участник вместо комнаты): первое запись остаётся как была.
        var second = await PostSignedAsync(LiveKitBodies.ParticipantJoined(id, room, "mallory-1"));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var row = Assert.Single(await RowsAsync(room));
        Assert.Equal("room_started", row.Type);
        Assert.Null(row.ParticipantIdentity);
    }

    [Fact]
    public async Task Twelve_simultaneous_deliveries_of_one_event_make_exactly_one_row_and_all_answer_200()
    {
        var room = LiveKitBodies.NewRoom();
        var id = LiveKitBodies.NewEventId();
        var body = LiveKitBodies.ParticipantJoined(id, room, "alice-1");

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => PostSignedAsync(body)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));   // ни одной ошибки уникальности наружу
        Assert.Equal(1, await CountByIdAsync(id));
    }

    [Fact]
    public async Task Many_different_events_at_once_are_all_stored()
    {
        var room = LiveKitBodies.NewRoom();
        var bodies = Enumerable.Range(0, 25)
            .Select(i => LiveKitBodies.ParticipantJoined(LiveKitBodies.NewEventId(), room, $"user-{i}", createdAt: 1794940500 + i))
            .ToList();

        var responses = await Task.WhenAll(bodies.Select(b => PostSignedAsync(b)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(25, (await RowsAsync(room)).Count);
    }

    [Theory]
    [InlineData("application/webhook+json")]   // так присылает LiveKit
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task The_content_type_does_not_matter_the_signed_bytes_do(string contentType)
    {
        var room = LiveKitBodies.NewRoom();
        var body = LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), room);

        var response = await _client.SendAsync(Request(body, Sign(body), contentType));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await RowsAsync(room));
    }

    [Fact]
    public async Task Non_ascii_text_is_stored_intact()
    {
        var room = "dev-комната-" + Guid.NewGuid().ToString("N")[..6];

        await PostSignedAsync(LiveKitBodies.ParticipantJoined(LiveKitBodies.NewEventId(), room, "алиса-1", "Алиса 🙂"));

        var row = Assert.Single(await RowsAsync(room));
        Assert.Equal("алиса-1", row.ParticipantIdentity);
    }

    // ---------- подпись: без неё события нет ----------

    private string Header(string variant, string body) => variant switch
    {
        "no header" => null!,
        "garbage" => "definitely-not-a-jwt",
        "bearer prefix" => "Bearer " + Sign(body),
        "wrong secret" => Sign(body, new LiveKitSigner.Options(Secret: "another-secret-another-secret-another-secret")),
        "wrong issuer" => Sign(body, new LiveKitSigner.Options(Issuer: "someone-else")),
        "expired" => LiveKitSigner.Sign(body, Now.AddMinutes(-10)),
        "not yet valid" => Sign(body, new LiveKitSigner.Options(NotBeforeOffset: TimeSpan.FromMinutes(10))),
        "hash of another body" => Sign(body, new LiveKitSigner.Options(HashOf: LiveKitBodies.RoomFinished("EV_other", "dev-other"))),
        "no hash" => Sign(body, new LiveKitSigner.Options(OmitHash: true)),
        "no expiry" => Sign(body, new LiveKitSigner.Options(OmitExpiry: true)),
        "alg none" => LiveKitSigner.Unsigned(body, Now),
        "alg HS512" => LiveKitSigner.SignWithAlgorithm(body, Now, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha512),
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    private static readonly string[] BadSignatureNames =
    [
        "no header", "garbage", "bearer prefix", "wrong secret", "wrong issuer", "expired", "not yet valid",
        "hash of another body", "no hash", "no expiry", "alg none", "alg HS512",
    ];

    public static TheoryData<string> BadSignatures
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in BadSignatureNames)
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(BadSignatures))]
    public async Task Without_a_valid_signature_the_answer_is_401_and_nothing_is_stored(string variant)
    {
        var room = LiveKitBodies.NewRoom();
        var id = LiveKitBodies.NewEventId();
        var body = LiveKitBodies.RoomStarted(id, room);

        var response = await _client.SendAsync(Request(body, Header(variant, body)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_webhook_signature", problem.GetProperty("code").GetString());
        Assert.Empty(await RowsAsync(room));
        Assert.Equal(0, await CountByIdAsync(id));
    }

    [Fact]
    public async Task Every_kind_of_signature_failure_gets_the_same_answer_so_the_response_reveals_nothing()
    {
        var body = LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), LiveKitBodies.NewRoom());
        var seen = new HashSet<string>();

        foreach (var variant in BadSignatureNames)
        {
            var response = await _client.SendAsync(Request(body, Header(variant, body)));
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            seen.Add($"{(int)response.StatusCode}|{problem.GetProperty("title")}|{problem.GetProperty("detail")}|{problem.GetProperty("code")}");
        }

        Assert.Single(seen);
    }

    [Fact]
    public async Task A_valid_signature_cannot_be_reused_for_a_changed_body()
    {
        var room = LiveKitBodies.NewRoom();
        var original = LiveKitBodies.ParticipantJoined(LiveKitBodies.NewEventId(), room, "alice-1");
        var tampered = original.Replace("alice-1", "mallory-1");
        var signature = Sign(original);   // перехвачена подпись настоящего события

        var response = await _client.SendAsync(Request(tampered, signature));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await RowsAsync(room));
    }

    [Fact]
    public async Task A_replayed_old_signed_event_is_harmless()
    {
        var room = LiveKitBodies.NewRoom();
        var body = LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), room);
        var signature = Sign(body);
        await _client.SendAsync(Request(body, signature));

        var replay = await _client.SendAsync(Request(body, signature));   // перехватчик шлёт то же ещё раз

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Single(await RowsAsync(room));   // повтор ничего не меняет
    }

    // ---------- подпись верна, тело не годится ----------

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"event":"room_started"}""")]
    [InlineData("""{"id":"EV_x"}""")]
    [InlineData("""{"event":"ROOM_STARTED","id":"EV_x"}""")]
    [InlineData("")]
    public async Task A_signed_body_that_is_not_a_livekit_event_is_400_and_stores_nothing(string body)
    {
        var before = await _factory.QueryAsync(db => db.Set<LiveKitWebhookEvent>().CountAsync());

        var response = await PostSignedAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_webhook_payload", problem.GetProperty("code").GetString());
        Assert.Equal(before, await _factory.QueryAsync(db => db.Set<LiveKitWebhookEvent>().CountAsync()));
    }

    // ---------- LiveKit не настроен: закрыто ----------

    [Fact]
    public async Task Without_a_configured_secret_even_a_well_formed_signed_event_is_refused()
    {
        var room = LiveKitBodies.NewRoom();
        var body = LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), room);
        using var app = _factory.WithSettings(null, ("LiveKit:ApiSecret", ""));
        var client = app.CreateClient();

        // Подпись настоящая (секретом теста), но приложение секрета не знает: «пустой ключ» не пропуск, а отказ.
        var response = await client.SendAsync(Request(body, Sign(body)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await RowsAsync(room));
    }

    [Fact]
    public async Task A_secret_shorter_than_the_minimum_stops_the_application_at_startup()
    {
        using var app = _factory.WithSettings(null, ("LiveKit:ApiSecret", "too-short"));

        var ex = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        Assert.Contains("LiveKit:ApiSecret", ex.ToString());
    }

    // ---------- размер ----------

    [Fact]
    public async Task A_body_over_256_kilobytes_is_413_whatever_its_signature()
    {
        var room = LiveKitBodies.NewRoom();
        var body = Padded(LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), room), 262144 + 1);

        var response = await PostSignedAsync(body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("payload_too_large", problem.GetProperty("code").GetString());
        Assert.Empty(await RowsAsync(room));
    }

    [Fact]
    public async Task A_body_of_exactly_256_kilobytes_is_still_fine()
    {
        var room = LiveKitBodies.NewRoom();
        var body = Padded(LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), room), 262144);

        var response = await PostSignedAsync(body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await RowsAsync(room));
    }

    [Fact]
    public async Task The_size_limit_holds_for_a_chunked_upload_without_a_content_length()
    {
        var body = Padded(LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), LiveKitBodies.NewRoom()), 300 * 1024);
        var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body))),
        };
        request.Headers.TryAddWithoutValidation("Authorization", Sign(body));
        request.Content.Headers.ContentLength = null;
        request.Headers.TransferEncodingChunked = true;

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>Делает JSON ровно нужной длины в байтах, добавляя лишнее поле: событие остаётся настоящим.</summary>
    private static string Padded(string json, int totalBytes)
    {
        const string field = ",\"pad\":\"\"}";
        var withoutBrace = json[..^1];
        var missing = totalBytes - Encoding.UTF8.GetByteCount(withoutBrace) - Encoding.UTF8.GetByteCount(field);
        return withoutBrace + ",\"pad\":\"" + new string('x', missing) + "\"}";
    }

    // ---------- маршрут ----------

    [Fact]
    public async Task Only_post_is_allowed()
    {
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.PutAsync(Url, new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.DeleteAsync(Url)).StatusCode);
    }

    [Fact]
    public async Task Other_webhook_paths_do_not_exist()
    {
        var response = await _client.PostAsync("/api/v1/webhooks/other", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- защиты других эндпоинтов его не касаются ----------

    [Fact]
    public async Task The_webhook_is_neither_rate_limited_nor_behind_the_captcha()
    {
        using var app = _factory.WithSettings(null, ("RateLimiting:Enabled", "true"), ("Turnstile:Enabled", "true"));
        var client = app.CreateClient();
        var room = LiveKitBodies.NewRoom();

        // Больше любого лимита на IP (самый строгий для анонимных — 10 в минуту) и без токена капчи.
        for (var i = 0; i < 60; i++)
        {
            var response = await PostSignedAsync(LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), room), client: client);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(60, (await RowsAsync(room)).Count);
    }

    [Fact]
    public async Task The_webhook_works_in_every_environment_unlike_the_dev_tools()
    {
        var room = LiveKitBodies.NewRoom();
        using var dev = _factory.WithSettings("Development");
        using var production = _factory.WithSettings("Production",
            ("Turnstile:SecretKey", "0x4AAAAAAAxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"));   // в Production тестовый секрет капчи не принимается

        var inDev = await PostSignedAsync(LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), room), client: dev.CreateClient());
        var inProduction = await PostSignedAsync(LiveKitBodies.RoomFinished(LiveKitBodies.NewEventId(), room), client: production.CreateClient());

        Assert.Equal(HttpStatusCode.OK, inDev.StatusCode);
        Assert.Equal(HttpStatusCode.OK, inProduction.StatusCode);
        Assert.Equal(2, (await RowsAsync(room)).Count);
    }

    // ---------- HTTPS: в Development webhook по http допустим ----------

    [Fact]
    public async Task In_development_the_webhook_is_not_redirected_to_https_but_other_paths_are()
    {
        using var app = _factory.WithSettings("Development", ("HTTPS_PORT", "443"));
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var room = LiveKitBodies.NewRoom();

        var webhook = await PostSignedAsync(LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), room), client: client);
        var other = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "a@b.c", password = "x" });

        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);                       // LiveKit в Docker ходит по http
        Assert.Equal(HttpStatusCode.TemporaryRedirect, other.StatusCode);           // а остальное по-прежнему на https
        Assert.StartsWith("https://", other.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Outside_development_the_webhook_follows_the_normal_https_redirect()
    {
        using var app = _factory.WithSettings("Production", ("HTTPS_PORT", "443"),
            ("Turnstile:SecretKey", "0x4AAAAAAAxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"));
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var body = LiveKitBodies.RoomStarted(LiveKitBodies.NewEventId(), LiveKitBodies.NewRoom());

        var response = await PostSignedAsync(body, client: client);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);   // исключение только для Development
    }

    // ---------- уникальность держит и сама БД ----------

    [Fact]
    public async Task The_database_itself_refuses_a_second_row_with_the_same_event_id()
    {
        var id = LiveKitBodies.NewEventId();

        await _factory.QueryAsync(async db =>
        {
            db.Add(LiveKitWebhookEvent.Create(id, "room_started", "dev-x", null, DateTime.UtcNow));
            return await db.SaveChangesAsync();
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _factory.QueryAsync(async db =>
        {
            db.Add(LiveKitWebhookEvent.Create(id, "room_finished", "dev-x", null, DateTime.UtcNow));
            return await db.SaveChangesAsync();
        }));
        Assert.Equal(1, await CountByIdAsync(id));
    }
}
