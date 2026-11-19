using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>POST /sessions/{id}/join-token (разделы 6.3 и 7.2 ТЗ): только двое участников, только в окне комнаты, не в замороженном обмене.</summary>
[Collection(ApiCollection.Name)]
public class VideoRoomTokenTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public VideoRoomTokenTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private async Task<HttpResponseMessage> SendAsync(MatchPerson? who, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid SessionId)> ScheduledLessonAsync(TimeSpan startsIn)
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id || u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });

        var created = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var exchangeId = (await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept"))).GetProperty("exchangeId").GetGuid();

        var proposed = await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions",
            new { startAt = Now.AddHours(5).ToString("O"), teacherId = a.Id });
        var sessionId = (await JsonAsync(proposed)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(b, HttpMethod.Post, $"/sessions/{sessionId}/confirm")).StatusCode);

        await MoveAsync(sessionId, Now + startsIn);
        return (a, b, exchangeId, sessionId);
    }

    private Task MoveAsync(Guid id, DateTime startAt) => _factory.QueryAsync(async db =>
    {
        await db.Sessions.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartAt, startAt).SetProperty(x => x.EndAt, startAt.AddMinutes(45)));
        return 0;
    });

    private Task SetStatusAsync(Guid id, SessionStatus status) => _factory.QueryAsync(async db =>
    {
        await db.Sessions.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status));
        return 0;
    });

    private Task<HttpResponseMessage> TokenAsync(MatchPerson? who, Guid sessionId) =>
        SendAsync(who, HttpMethod.Post, $"/sessions/{sessionId}/join-token");

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    private static async Task<JsonWebToken> ValidateAsync(string token)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = ApiFactory.LiveKitApiKey,
            ValidateAudience = false,
            ValidateLifetime = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.LiveKitApiSecret)),
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        return (JsonWebToken)result.SecurityToken;
    }

    // ---------- выдача ----------

    [Fact]
    public async Task A_participant_gets_a_token_for_the_lesson_room_with_the_times_the_front_end_needs()
    {
        var (a, _, _, sessionId) = await ScheduledLessonAsync(TimeSpan.FromMinutes(5));

        var response = await TokenAsync(a, sessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var body = await JsonAsync(response);
        var start = Now.AddMinutes(5);
        Assert.Equal($"session-{sessionId}", body.GetProperty("roomName").GetString());
        Assert.Equal(ApiFactory.LiveKitUrl, body.GetProperty("url").GetString());
        Assert.Equal(start, body.GetProperty("startAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(start.AddMinutes(45), body.GetProperty("plannedEndAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(start.AddMinutes(75), body.GetProperty("roomClosesAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
        Assert.True(body.GetProperty("expiresAt").GetDateTime() > Now);
        Assert.True(body.GetProperty("expiresAt").GetDateTime() <= Now.AddMinutes(6));

        var jwt = await ValidateAsync(body.GetProperty("token").GetString()!);
        Assert.Equal(a.Id.ToString(), jwt.Subject);
        var video = jwt.GetPayloadValue<JsonElement>("video");
        Assert.Equal($"session-{sessionId}", video.GetProperty("room").GetString());
        Assert.True(video.GetProperty("roomJoin").GetBoolean());
        Assert.False(video.TryGetProperty("roomAdmin", out _));   // комнату участник удалить не может
    }

    [Fact]
    public async Task Both_participants_get_tokens_with_their_own_identity()
    {
        var (a, b, _, sessionId) = await ScheduledLessonAsync(TimeSpan.FromMinutes(5));

        var tokenA = await ValidateAsync((await JsonAsync(await TokenAsync(a, sessionId))).GetProperty("token").GetString()!);
        var tokenB = await ValidateAsync((await JsonAsync(await TokenAsync(b, sessionId))).GetProperty("token").GetString()!);

        Assert.Equal(a.Id.ToString(), tokenA.Subject);
        Assert.Equal(b.Id.ToString(), tokenB.Subject);
        Assert.NotEqual(tokenA.GetPayloadValue<string>("jti"), tokenB.GetPayloadValue<string>("jti"));
    }

    [Fact]
    public async Task An_outsider_and_an_anonymous_caller_get_nothing_and_a_stranger_sees_no_lesson()
    {
        var (_, _, _, sessionId) = await ScheduledLessonAsync(TimeSpan.FromMinutes(5));
        var world = await MatchTestWorld.CreateAsync(_factory);
        var outsider = await world.UserAsync(Beginner0, Beginner1);

        var denied = await TokenAsync(outsider, sessionId);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal("session_not_found", await CodeAsync(denied));

        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenAsync(null, sessionId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TokenAsync(outsider, Guid.NewGuid())).StatusCode);
    }

    // ---------- окно ----------

    [Theory]
    [InlineData(10, true)]       // ровно за 10 минут до начала: открыто
    [InlineData(11, false)]      // за 11 минут: ещё рано
    [InlineData(-45, true)]      // плановое окончание
    [InlineData(-74, true)]      // за минуту до закрытия
    [InlineData(-76, false)]     // закрыто
    public async Task The_token_is_given_only_inside_the_room_window(int minutesUntilStart, bool allowed)
    {
        var (a, _, _, sessionId) = await ScheduledLessonAsync(TimeSpan.FromMinutes(minutesUntilStart));

        var response = await TokenAsync(a, sessionId);

        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Too_early_says_when_the_door_opens()
    {
        var (a, _, _, sessionId) = await ScheduledLessonAsync(TimeSpan.FromMinutes(30));

        var response = await TokenAsync(a, sessionId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("join_window_not_open", body.GetProperty("code").GetString());
        Assert.Equal(Now.AddMinutes(20), body.GetProperty("opensAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Too_late_says_when_the_door_closed()
    {
        var (a, _, _, sessionId) = await ScheduledLessonAsync(TimeSpan.FromMinutes(-80));

        var response = await TokenAsync(a, sessionId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("join_window_closed", body.GetProperty("code").GetString());
        Assert.Equal(Now.AddMinutes(-5), body.GetProperty("closedAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
    }

    // ---------- статусы урока и обмена ----------

    [Theory]
    [InlineData(SessionStatus.InProgress, true)]
    [InlineData(SessionStatus.NoShow, true)]      // опоздавший может зайти
    [InlineData(SessionStatus.Missed, true)]
    [InlineData(SessionStatus.Proposed, false)]
    [InlineData(SessionStatus.Declined, false)]
    [InlineData(SessionStatus.Cancelled, false)]
    [InlineData(SessionStatus.LateCancelled, false)]
    [InlineData(SessionStatus.Completed, false)]
    [InlineData(SessionStatus.Unconfirmed, false)]
    [InlineData(SessionStatus.NotCounted, false)]
    [InlineData(SessionStatus.Expired, false)]
    public async Task The_token_depends_on_the_lesson_status(SessionStatus status, bool allowed)
    {
        var (a, _, _, sessionId) = await ScheduledLessonAsync(TimeSpan.FromMinutes(-20));
        await SetStatusAsync(sessionId, status);

        var response = await TokenAsync(a, sessionId);

        if (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await JsonAsync(response);
            Assert.Equal("session_not_joinable", body.GetProperty("code").GetString());
            Assert.Equal(status.ToString(), body.GetProperty("sessionStatus").GetString());
        }
    }

    [Fact]
    public async Task A_frozen_exchange_gets_no_token()
    {
        var (a, b, exchangeId, sessionId) = await ScheduledLessonAsync(TimeSpan.FromMinutes(-5));
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExchangeStatus.Frozen));
            return 0;
        });

        foreach (var who in new[] { a, b })
        {
            var response = await TokenAsync(who, sessionId);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("exchange_frozen", await CodeAsync(response));
        }
    }

    [Theory]
    [InlineData(ExchangeStatus.Aborted)]
    [InlineData(ExchangeStatus.Completed)]
    public async Task A_closed_exchange_gets_no_token(ExchangeStatus status)
    {
        var (a, _, exchangeId, sessionId) = await ScheduledLessonAsync(TimeSpan.FromMinutes(-5));
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, status));
            return 0;
        });

        var response = await TokenAsync(a, sessionId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("exchange_not_active", await CodeAsync(response));
    }

    // ---------- аккаунт и настройки ----------

    [Fact]
    public async Task A_blocked_account_cannot_get_a_token()
    {
        var (a, _, _, sessionId) = await ScheduledLessonAsync(TimeSpan.FromMinutes(-5));
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Blocked));
            return 0;
        });

        var response = await TokenAsync(a, sessionId);

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Without_livekit_settings_the_answer_is_service_unavailable()
    {
        var (a, _, _, sessionId) = await ScheduledLessonAsync(TimeSpan.FromMinutes(-5));

        using var factory = _factory.WithSettings("Testing", ("LiveKit:ApiSecret", ""));
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/join-token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", a.Token);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("livekit_not_configured", await CodeAsync(response));
    }

    // ---------- имя комнаты ----------

    [Fact]
    public void The_room_name_round_trips_and_foreign_names_are_rejected()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, VideoRoomService.TryParseRoomName(VideoRoomService.RoomName(id)));
        Assert.Null(VideoRoomService.TryParseRoomName("dev-test-room"));
        Assert.Null(VideoRoomService.TryParseRoomName("session-"));
        Assert.Null(VideoRoomService.TryParseRoomName("session-not-a-guid"));
        Assert.Null(VideoRoomService.TryParseRoomName(null));
        Assert.Null(VideoRoomService.TryParseRoomName($"SESSION-{id}"));
    }
}
