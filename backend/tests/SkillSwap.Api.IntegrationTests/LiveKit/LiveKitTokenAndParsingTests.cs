using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.LiveKit;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.LiveKit;

namespace SkillSwap.Api.IntegrationTests.LiveKit;

/// <summary>Токен доступа в комнату LiveKit: содержимое, срок, подпись.</summary>
public class LiveKitTokenServiceTests
{
    private const string Secret = ApiFactory.LiveKitApiSecret;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    private LiveKitTokenService Service(string? secret = Secret, int ttl = 300) =>
        new(Options.Create(new LiveKitOptions { ApiKey = "devkey", ApiSecret = secret, TokenTtlSeconds = ttl }), _clock);

    private static async Task<JsonWebToken> ValidateAsync(string token, string secret)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "devkey",
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = false,
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        return (JsonWebToken)result.SecurityToken;
    }

    [Fact]
    public async Task The_token_carries_the_identity_the_room_and_the_join_rights_in_the_livekit_format()
    {
        var token = Service().CreateJoinToken("dev-room", "alice-1", "Алиса");

        var jwt = await ValidateAsync(token.Value, Secret);
        Assert.Equal("devkey", jwt.Issuer);
        Assert.Equal("alice-1", jwt.Subject);
        Assert.Equal("alice-1", jwt.Id);
        Assert.Equal("Алиса", jwt.GetPayloadValue<string>("name"));

        var video = jwt.GetPayloadValue<JsonElement>("video");
        Assert.Equal("dev-room", video.GetProperty("room").GetString());
        Assert.True(video.GetProperty("roomJoin").GetBoolean());
        Assert.True(video.GetProperty("canPublish").GetBoolean());
        Assert.True(video.GetProperty("canSubscribe").GetBoolean());
        Assert.True(video.GetProperty("canPublishData").GetBoolean());
    }

    [Fact]
    public async Task The_token_gives_no_administrative_rights()
    {
        var token = Service().CreateJoinToken("dev-room", "alice-1", null);

        var video = (await ValidateAsync(token.Value, Secret)).GetPayloadValue<JsonElement>("video");

        var granted = video.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("roomAdmin", granted);
        Assert.DoesNotContain("roomCreate", granted);
        Assert.DoesNotContain("roomList", granted);
        Assert.DoesNotContain("roomRecord", granted);
        Assert.DoesNotContain("hidden", granted);
        Assert.DoesNotContain("recorder", granted);
    }

    [Fact]
    public async Task The_token_is_short_lived_five_minutes_by_default()
    {
        var token = Service().CreateJoinToken("dev-room", "alice-1", null);

        var jwt = await ValidateAsync(token.Value, Secret);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(5), token.ExpiresAt);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, jwt.ValidFrom);
        Assert.Equal(TimeSpan.FromMinutes(5), jwt.ValidTo - jwt.ValidFrom);
    }

    [Fact]
    public void The_lifetime_follows_the_configuration()
    {
        var token = Service(ttl: 60).CreateJoinToken("dev-room", "alice-1", null);

        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddSeconds(60), token.ExpiresAt);
    }

    [Fact]
    public async Task A_token_is_checkable_only_with_the_right_secret()
    {
        var token = Service().CreateJoinToken("dev-room", "alice-1", null);

        await Assert.ThrowsAsync<Xunit.Sdk.TrueException>(() => ValidateAsync(token.Value, "some-other-secret-some-other-secret-1234"));
    }

    [Fact]
    public async Task Without_a_display_name_the_name_claim_is_absent()
    {
        var token = Service().CreateJoinToken("dev-room", "alice-1", "  ");

        var jwt = await ValidateAsync(token.Value, Secret);
        Assert.False(jwt.TryGetPayloadValue<string>("name", out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short-secret")]
    public void Without_a_proper_secret_no_token_is_issued(string? secret)
    {
        Assert.Throws<InvalidOperationException>(() => Service(secret).CreateJoinToken("dev-room", "alice-1", null));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0123456789012345678901234567890", false)]    // 31 символ
    [InlineData("01234567890123456789012345678901", true)]    // 32 символа
    public void IsConfigured_needs_a_secret_of_at_least_32_characters(string? secret, bool expected)
    {
        Assert.Equal(expected, new LiveKitOptions { ApiKey = "devkey", ApiSecret = secret }.IsConfigured);
    }

    [Fact]
    public void IsConfigured_needs_a_key_name_too()
    {
        Assert.False(new LiveKitOptions { ApiKey = "", ApiSecret = Secret }.IsConfigured);
        Assert.False(new LiveKitOptions { ApiKey = "  ", ApiSecret = Secret }.IsConfigured);
    }

    [Fact]
    public void Defaults_point_at_the_local_container_and_a_five_minute_token()
    {
        var o = new LiveKitOptions();

        Assert.Equal("ws://localhost:7880", o.Url);
        Assert.Equal("devkey", o.ApiKey);
        Assert.Equal(300, o.TokenTtlSeconds);
        Assert.Null(o.ApiSecret);   // секрета в коде нет
        Assert.False(o.IsConfigured);
    }
}

/// <summary>Разбор событий LiveKit: настоящие тела, терпимость к лишнему, строгость к нужному.</summary>
public class LiveKitWebhookParsingTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static LiveKitWebhookEvent? Parse(string body) => LiveKitWebhookService.Parse(Encoding.UTF8.GetBytes(body), Now);

    [Fact]
    public void A_room_started_event_has_a_room_and_no_participant()
    {
        var e = Parse(LiveKitBodies.RoomStarted("EV_1", "dev-room"))!;

        Assert.Equal("EV_1", e.EventId);
        Assert.Equal("room_started", e.Type);
        Assert.Equal("dev-room", e.RoomName);
        Assert.Null(e.ParticipantIdentity);
        Assert.Null(e.ProcessedAt);
    }

    [Fact]
    public void A_participant_event_carries_the_identity()
    {
        var e = Parse(LiveKitBodies.ParticipantJoined("EV_2", "dev-room", "alice-1"))!;

        Assert.Equal("participant_joined", e.Type);
        Assert.Equal("alice-1", e.ParticipantIdentity);
        Assert.Equal("dev-room", e.RoomName);
    }

    [Theory]
    [InlineData("participant_left")]
    [InlineData("participant_connection_aborted")]
    [InlineData("track_published")]
    [InlineData("egress_started")]
    [InlineData("ingress_started")]
    [InlineData("some_future_event_2")]
    public void Event_types_including_new_ones_are_accepted(string type)
    {
        var body = $$"""{"event":"{{type}}","id":"EV_x","createdAt":"1794940517"}""";

        Assert.Equal(type, Parse(body)!.Type);
    }

    [Fact]
    public void The_time_comes_from_createdAt_given_as_a_string_like_protobuf_does()
    {
        var e = Parse("""{"event":"room_started","id":"EV_t","createdAt":"1794940517"}""")!;

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794940517).UtcDateTime, e.OccurredAt);
        Assert.Equal(DateTimeKind.Utc, e.OccurredAt.Kind);
    }

    [Fact]
    public void The_time_may_also_be_a_plain_number()
    {
        var e = Parse("""{"event":"room_started","id":"EV_t","createdAt":1794940517}""")!;

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794940517).UtcDateTime, e.OccurredAt);
    }

    [Theory]
    [InlineData("")]                                // нет поля вовсе
    [InlineData(",\"createdAt\":\"soon\"")]
    [InlineData(",\"createdAt\":\"-5\"")]
    [InlineData(",\"createdAt\":\"0\"")]             // 1970 год: не наше время
    [InlineData(",\"createdAt\":\"99999999999\"")]   // далеко в будущем
    [InlineData(",\"createdAt\":null")]
    [InlineData(",\"createdAt\":true")]
    [InlineData(",\"createdAt\":1.5")]
    public void A_missing_or_unreasonable_time_falls_back_to_the_time_of_receipt(string createdAtField)
    {
        var e = Parse("{\"event\":\"room_started\",\"id\":\"EV_t\"" + createdAtField + "}")!;

        Assert.Equal(Now, e.OccurredAt);
    }

    [Fact]
    public void Unknown_fields_and_nested_noise_are_ignored()
    {
        var e = Parse("""{"event":"room_started","id":"EV_n","createdAt":"1794940517","numDropped":3,"track":{"sid":"TR_1","x":[1,2,{"y":null}]},"future":{"a":"b"}}""")!;

        Assert.Equal("EV_n", e.EventId);
    }

    [Fact]
    public void Non_ascii_identities_and_rooms_survive()
    {
        var e = Parse(LiveKitBodies.ParticipantJoined("EV_u", "dev-комната", "алиса-1"))!;

        Assert.Equal("dev-комната", e.RoomName);
        Assert.Equal("алиса-1", e.ParticipantIdentity);
    }

    // ---------- обязательное ----------

    [Theory]
    [InlineData("""{"event":"room_started"}""")]                                    // нет id
    [InlineData("""{"id":"EV_1"}""")]                                                // нет event
    [InlineData("""{"event":"room_started","id":""}""")]
    [InlineData("""{"event":"","id":"EV_1"}""")]
    [InlineData("""{"event":"room_started","id":"   "}""")]
    [InlineData("""{"event":"room_started","id":123}""")]                           // id не строка
    [InlineData("""{"event":5,"id":"EV_1"}""")]
    [InlineData("""{"event":null,"id":"EV_1"}""")]
    [InlineData("""{"event":"Room_Started","id":"EV_1"}""")]                        // заглавные
    [InlineData("""{"event":"room started","id":"EV_1"}""")]                        // пробел
    [InlineData("""{"event":"room-started","id":"EV_1"}""")]                        // дефис
    [InlineData("""{"event":"room_started<script>","id":"EV_1"}""")]
    public void Events_without_a_usable_id_or_type_are_rejected(string body)
    {
        Assert.Null(Parse(body));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("{\"event\":\"room_started\",\"id\":\"EV_1\"")]   // оборван
    public void A_body_that_is_not_a_json_object_is_rejected_without_throwing(string body)
    {
        Assert.Null(Parse(body));
    }

    // ---------- пределы длины ----------

    [Fact]
    public void Lengths_up_to_the_column_limits_are_accepted_and_longer_ones_rejected()
    {
        string Body(string id = "EV_1", string type = "room_started", string room = "dev-room", string identity = "alice") =>
            $$"""{"event":"{{type}}","id":"{{id}}","room":{"name":"{{room}}"},"participant":{"identity":"{{identity}}"},"createdAt":"1794940517"}""";

        Assert.NotNull(Parse(Body(id: new string('a', LiveKitWebhookService.EventIdMaxLength))));
        Assert.Null(Parse(Body(id: new string('a', LiveKitWebhookService.EventIdMaxLength + 1))));

        Assert.NotNull(Parse(Body(type: new string('a', LiveKitWebhookService.TypeMaxLength))));
        Assert.Null(Parse(Body(type: new string('a', LiveKitWebhookService.TypeMaxLength + 1))));

        Assert.NotNull(Parse(Body(room: new string('r', LiveKitWebhookService.RoomNameMaxLength))));
        Assert.Null(Parse(Body(room: new string('r', LiveKitWebhookService.RoomNameMaxLength + 1))));

        Assert.NotNull(Parse(Body(identity: new string('i', LiveKitWebhookService.IdentityMaxLength))));
        Assert.Null(Parse(Body(identity: new string('i', LiveKitWebhookService.IdentityMaxLength + 1))));
    }

    [Theory]
    [InlineData("""{"event":"room_started","id":"EV_1","room":"just-a-string"}""")]
    [InlineData("""{"event":"room_started","id":"EV_1","room":{"name":5}}""")]
    [InlineData("""{"event":"room_started","id":"EV_1","room":null,"participant":[]}""")]
    [InlineData("""{"event":"room_started","id":"EV_1","participant":{"name":"no identity here"}}""")]
    public void Odd_room_and_participant_shapes_just_mean_the_fields_are_absent(string body)
    {
        var e = Parse(body)!;

        Assert.Null(e.RoomName);
        Assert.Null(e.ParticipantIdentity);
    }
}

/// <summary>Порядок работы сервиса приёма: сначала подпись, и только потом хоть что-то из тела.</summary>
public class LiveKitWebhookServiceTests
{
    private sealed class FakeVerifier(bool accept) : ILiveKitWebhookVerifier
    {
        public string? LastHeader { get; private set; }
        public byte[]? LastBody { get; private set; }
        public int Calls { get; private set; }

        public Task<bool> VerifyAsync(string? authorizationHeader, ReadOnlyMemory<byte> body)
        {
            Calls++;
            LastHeader = authorizationHeader;
            LastBody = body.ToArray();
            return Task.FromResult(accept);
        }
    }

    private sealed class FakeProcessor : ILiveKitEventProcessor
    {
        public List<string> Processed { get; } = [];

        public Task ProcessAsync(string eventId, CancellationToken ct)
        {
            Processed.Add(eventId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStore(bool isNew) : ILiveKitWebhookEventStore
    {
        public List<LiveKitWebhookEvent> Added { get; } = [];

        public Task<bool> TryAddAsync(LiveKitWebhookEvent webhookEvent, CancellationToken ct = default)
        {
            Added.Add(webhookEvent);
            return Task.FromResult(isNew);
        }

        public Task<LiveKitWebhookEvent?> FindAsync(string eventId, CancellationToken ct = default) => Task.FromResult<LiveKitWebhookEvent?>(null);

        public Task<IReadOnlyList<LiveKitWebhookEvent>> ListByRoomAsync(string roomName, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LiveKitWebhookEvent>>([]);

        public Task<IReadOnlyList<string>> ListUnprocessedIdsAsync(string roomName, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private static byte[] Body(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task A_bad_signature_is_unauthorized_and_nothing_is_read_or_stored()
    {
        var store = new FakeStore(true);
        var service = new LiveKitWebhookService(new FakeVerifier(false), store, Clock, new FakeProcessor());

        // Тело заведомо мусорное: при плохой подписи важно, что ответ «нет доступа», а не «плохое тело».
        var result = await service.ReceiveAsync("token", Body("not json at all"));

        Assert.Equal(WebhookResult.Unauthorized, result);
        Assert.Empty(store.Added);
    }

    [Fact]
    public async Task The_verifier_gets_exactly_the_header_and_the_raw_bytes()
    {
        var verifier = new FakeVerifier(true);
        var service = new LiveKitWebhookService(verifier, new FakeStore(true), Clock, new FakeProcessor());
        var body = Body(LiveKitBodies.RoomStarted("EV_1", "dev-room"));

        await service.ReceiveAsync("the-header", body);

        Assert.Equal(1, verifier.Calls);
        Assert.Equal("the-header", verifier.LastHeader);
        Assert.Equal(body, verifier.LastBody);
    }

    [Fact]
    public async Task A_good_signature_and_a_bad_body_is_malformed_and_stores_nothing()
    {
        var store = new FakeStore(true);
        var service = new LiveKitWebhookService(new FakeVerifier(true), store, Clock, new FakeProcessor());

        Assert.Equal(WebhookResult.Malformed, await service.ReceiveAsync("t", Body("{}")));
        Assert.Empty(store.Added);
    }

    [Fact]
    public async Task A_new_event_is_accepted_and_stored_with_its_fields()
    {
        var store = new FakeStore(true);
        var service = new LiveKitWebhookService(new FakeVerifier(true), store, Clock, new FakeProcessor());

        var result = await service.ReceiveAsync("t", Body(LiveKitBodies.ParticipantJoined("EV_9", "dev-room", "alice-1")));

        Assert.Equal(WebhookResult.Accepted, result);
        var stored = Assert.Single(store.Added);
        Assert.Equal(("EV_9", "participant_joined", "dev-room", "alice-1"),
            (stored.EventId, stored.Type, stored.RoomName, stored.ParticipantIdentity));
    }

    [Fact]
    public async Task A_repeated_event_is_reported_as_a_duplicate()
    {
        var service = new LiveKitWebhookService(new FakeVerifier(true), new FakeStore(isNew: false), Clock, new FakeProcessor());

        var result = await service.ReceiveAsync("t", Body(LiveKitBodies.RoomStarted("EV_1", "dev-room")));

        Assert.Equal(WebhookResult.Duplicate, result);
    }
}
