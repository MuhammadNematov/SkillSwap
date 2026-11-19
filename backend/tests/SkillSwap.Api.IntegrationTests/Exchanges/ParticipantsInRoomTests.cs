using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.LiveKit;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>participantsInRoom: кто сейчас в видеокомнате. В событии sessionUpdated (хаб чата) и в GET-ответах по уроку; по нему фронтенд показывает «партнёр в комнате».</summary>
[Collection(ApiCollection.Name)]
public class ParticipantsInRoomTests
{
    private const string WebhookUrl = "/api/v1/webhooks/livekit";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ParticipantsInRoomTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private sealed record Lesson(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid SessionId, DateTime Start, long Ticks)
    {
        public string Room => $"session-{SessionId}";
    }

    private async Task<HttpResponseMessage> ApiAsync(MatchPerson who, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>Подтверждённый урок, начавшийся минуту назад (комната открыта).</summary>
    private async Task<Lesson> LessonAsync()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id || u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var created = await ApiAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var exchangeId = (await JsonAsync(await ApiAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept"))).GetProperty("exchangeId").GetGuid();
        var proposed = await ApiAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { startAt = Now.AddHours(5).ToString("O"), teacherId = a.Id });
        var sessionId = (await JsonAsync(proposed)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await ApiAsync(b, HttpMethod.Post, $"/sessions/{sessionId}/confirm")).StatusCode);

        var start = Now.AddMinutes(-1);
        await _factory.QueryAsync(async db =>
        {
            await db.Sessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartAt, start).SetProperty(x => x.EndAt, start.AddMinutes(45)));
            return 0;
        });
        var ticks = (await _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId))).StartAt.Ticks;
        return new Lesson(a, b, exchangeId, sessionId, new DateTime(ticks, DateTimeKind.Utc), ticks);
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private async Task PostEventAsync(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, WebhookUrl) { Content = new StringContent(body, new UTF8Encoding(false), "application/webhook+json") };
        request.Headers.TryAddWithoutValidation("Authorization", LiveKitSigner.Sign(body, Now));
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(request)).StatusCode);
    }

    private Task JoinedAsync(Lesson lesson, MatchPerson who, int minute) =>
        PostEventAsync(LiveKitBodies.ParticipantJoined(LiveKitBodies.NewEventId(), lesson.Room, who.Id.ToString("D"), createdAt: Unix(lesson.Start.AddMinutes(minute))));

    private Task LeftAsync(Lesson lesson, MatchPerson who, int minute) =>
        PostEventAsync(LiveKitBodies.ParticipantLeft(LiveKitBodies.NewEventId(), lesson.Room, who.Id.ToString("D"), createdAt: Unix(lesson.Start.AddMinutes(minute))));

    private async Task<List<Guid>> InRoomViaApiAsync(MatchPerson who, Guid sessionId) =>
        (await JsonAsync(await ApiAsync(who, HttpMethod.Get, $"/sessions/{sessionId}"))).GetProperty("participantsInRoom").EnumerateArray().Select(x => x.GetGuid()).ToList();

    private async Task<HubConnection> ConnectAsync(MatchPerson who, ConcurrentQueue<JsonElement> sessionEvents, Guid exchangeId)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(who.Token);
            })
            .Build();
        connection.On<JsonElement>("sessionUpdated", e => sessionEvents.Enqueue(e));
        await connection.StartAsync();
        await connection.InvokeAsync("Join", exchangeId);
        return connection;
    }

    private static async Task<JsonElement> WaitForAsync(ConcurrentQueue<JsonElement> events, Func<JsonElement, bool> match)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            foreach (var e in events.ToArray())
            {
                if (match(e))
                {
                    return e;
                }
            }

            await Task.Delay(40);
        }

        throw new Xunit.Sdk.XunitException("ожидаемое событие sessionUpdated не пришло");
    }

    private static List<Guid> InRoom(JsonElement session) =>
        session.GetProperty("participantsInRoom").EnumerateArray().Select(x => x.GetGuid()).ToList();

    // ---------- GET ----------

    [Fact]
    public async Task The_lesson_lists_who_is_in_the_room_in_get_session_and_in_the_exchange_list()
    {
        var lesson = await LessonAsync();
        Assert.Empty(await InRoomViaApiAsync(lesson.A, lesson.SessionId));   // поле есть всегда, пока пусто

        await JoinedAsync(lesson, lesson.A, 0);
        Assert.Equal([lesson.A.Id], await InRoomViaApiAsync(lesson.B, lesson.SessionId));

        await JoinedAsync(lesson, lesson.B, 1);
        Assert.Equal(new[] { lesson.A.Id, lesson.B.Id }.Order(), (await InRoomViaApiAsync(lesson.A, lesson.SessionId)).Order());
        var list = await JsonAsync(await ApiAsync(lesson.A, HttpMethod.Get, $"/exchanges/{lesson.ExchangeId}/sessions"));
        Assert.Equal(2, list.EnumerateArray().Single().GetProperty("participantsInRoom").GetArrayLength());

        await LeftAsync(lesson, lesson.A, 5);
        Assert.Equal([lesson.B.Id], await InRoomViaApiAsync(lesson.A, lesson.SessionId));

        await LeftAsync(lesson, lesson.B, 9);
        Assert.Empty(await InRoomViaApiAsync(lesson.A, lesson.SessionId));
    }

    [Fact]
    public async Task A_participant_who_reconnects_is_in_the_room_once()
    {
        var lesson = await LessonAsync();

        await JoinedAsync(lesson, lesson.A, 0);
        await LeftAsync(lesson, lesson.A, 3);
        await JoinedAsync(lesson, lesson.A, 4);

        Assert.Equal([lesson.A.Id], await InRoomViaApiAsync(lesson.B, lesson.SessionId));
    }

    // ---------- sessionUpdated ----------

    [Fact]
    public async Task The_partner_gets_session_updated_with_participants_in_room_when_someone_joins_and_leaves()
    {
        var lesson = await LessonAsync();
        var forB = new ConcurrentQueue<JsonElement>();
        await using var bConn = await ConnectAsync(lesson.B, forB, lesson.ExchangeId);

        await JoinedAsync(lesson, lesson.A, 0);

        var joined = await WaitForAsync(forB, e => InRoom(e).Contains(lesson.A.Id));
        Assert.Equal(lesson.SessionId, joined.GetProperty("id").GetGuid());
        Assert.Equal("InProgress", joined.GetProperty("status").GetString());   // первый вход начал урок: статус и присутствие в одном событии
        Assert.Equal([lesson.A.Id], InRoom(joined));

        await LeftAsync(lesson, lesson.A, 4);
        var left = await WaitForAsync(forB, e => e.GetProperty("status").GetString() == "InProgress" && InRoom(e).Count == 0);
        Assert.Equal(lesson.SessionId, left.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_second_join_without_a_status_change_still_sends_the_event_and_both_participants_get_it()
    {
        var lesson = await LessonAsync();
        var forA = new ConcurrentQueue<JsonElement>();
        var forB = new ConcurrentQueue<JsonElement>();
        await using var aConn = await ConnectAsync(lesson.A, forA, lesson.ExchangeId);
        await using var bConn = await ConnectAsync(lesson.B, forB, lesson.ExchangeId);
        await JoinedAsync(lesson, lesson.A, 0);
        await WaitForAsync(forB, e => InRoom(e).Count == 1);

        await JoinedAsync(lesson, lesson.B, 1);   // статус уже InProgress: меняется только присутствие

        foreach (var queue in new[] { forA, forB })
        {
            var both = await WaitForAsync(queue, e => InRoom(e).Count == 2);
            Assert.Equal(new[] { lesson.A.Id, lesson.B.Id }.Order(), InRoom(both).Order());
        }
    }

    [Fact]
    public async Task Closing_the_room_empties_the_list_and_the_event_says_so()
    {
        var lesson = await LessonAsync();
        await JoinedAsync(lesson, lesson.A, 0);
        await JoinedAsync(lesson, lesson.B, 0);
        await _factory.QueryAsync(async db =>
        {
            // Урок давно идёт: комната должна быть закрыта (старт − 80 минут), оба так и сидят в ней.
            var start = Now.AddMinutes(-80);
            await db.Sessions.Where(s => s.Id == lesson.SessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartAt, start).SetProperty(x => x.EndAt, start.AddMinutes(45)));
            return 0;
        });
        var moved = (await _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == lesson.SessionId))).StartAt.Ticks;
        var forB = new ConcurrentQueue<JsonElement>();
        await using var bConn = await ConnectAsync(lesson.B, forB, lesson.ExchangeId);

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<VideoLessonJobs>().CloseRoomAsync(lesson.SessionId, moved);
        }

        var closed = await WaitForAsync(forB, e => InRoom(e).Count == 0);
        Assert.Equal(lesson.SessionId, closed.GetProperty("id").GetGuid());
        Assert.Empty(await InRoomViaApiAsync(lesson.A, lesson.SessionId));
    }

    [Fact]
    public async Task A_stranger_does_not_receive_the_event_and_an_event_for_an_unknown_person_changes_nothing()
    {
        var lesson = await LessonAsync();
        var world = await MatchTestWorld.CreateAsync(_factory);
        var stranger = await world.UserAsync(Beginner0, Beginner1);
        var forStranger = new ConcurrentQueue<JsonElement>();
        await using var strangerConn = new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(stranger.Token);
            })
            .Build();
        strangerConn.On<JsonElement>("sessionUpdated", e => forStranger.Enqueue(e));
        await strangerConn.StartAsync();

        await JoinedAsync(lesson, lesson.A, 0);
        await PostEventAsync(LiveKitBodies.ParticipantJoined(LiveKitBodies.NewEventId(), lesson.Room, stranger.Id.ToString("D"), createdAt: Unix(lesson.Start.AddMinutes(1))));

        await Task.Delay(300);
        Assert.Empty(forStranger);
        Assert.Equal([lesson.A.Id], await InRoomViaApiAsync(lesson.B, lesson.SessionId));   // посторонний в комнату урока не попал
    }
}
