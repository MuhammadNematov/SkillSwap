using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.LiveKit;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>Присутствие в комнате урока из событий LiveKit (разделы 6.3 и 7.2 ТЗ): интервалы, переподключения, повторы, порядок, гонки.</summary>
[Collection(ApiCollection.Name)]
public class VideoAttendanceTests
{
    private const string Url = "/api/v1/webhooks/livekit";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public VideoAttendanceTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    /// <summary>Урок начался минуту назад: оба участника, комната открыта.</summary>
    private sealed record Lesson(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid SessionId, DateTime Start)
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

    private async Task<Lesson> ScheduledLessonAsync(bool confirm = true)
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
        if (confirm)
        {
            Assert.Equal(HttpStatusCode.OK, (await ApiAsync(b, HttpMethod.Post, $"/sessions/{sessionId}/confirm")).StatusCode);
        }

        var start = Now.AddMinutes(-1);
        await _factory.QueryAsync(async db =>
        {
            await db.Sessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartAt, start).SetProperty(x => x.EndAt, start.AddMinutes(45)));
            return 0;
        });
        return new Lesson(a, b, exchangeId, sessionId, start);
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private Task<HttpResponseMessage> PostAsync(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(body, new UTF8Encoding(false), "application/webhook+json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", LiveKitSigner.Sign(body, Now));
        return _client.SendAsync(request);
    }

    private async Task<string> JoinedAsync(Lesson lesson, MatchPerson who, DateTime at, string? room = null)
    {
        var id = LiveKitBodies.NewEventId();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(LiveKitBodies.ParticipantJoined(id, room ?? lesson.Room, who.Id.ToString("D"), createdAt: Unix(at)))).StatusCode);
        return id;
    }

    private async Task<string> LeftAsync(Lesson lesson, MatchPerson who, DateTime at)
    {
        var id = LiveKitBodies.NewEventId();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(LiveKitBodies.ParticipantLeft(id, lesson.Room, who.Id.ToString("D"), createdAt: Unix(at)))).StatusCode);
        return id;
    }

    private Task<List<SessionAttendance>> RowsAsync(Guid sessionId) =>
        _factory.QueryAsync(db => db.SessionAttendances.AsNoTracking().Where(a => a.SessionId == sessionId).OrderBy(a => a.JoinedAt).ToListAsync());

    private Task<SessionStatus> StatusAsync(Guid sessionId) =>
        _factory.QueryAsync(db => db.Sessions.Where(s => s.Id == sessionId).Select(s => s.Status).SingleAsync());

    private Task<LiveKitWebhookEvent> EventAsync(string eventId) =>
        _factory.QueryAsync(db => db.Set<LiveKitWebhookEvent>().AsNoTracking().SingleAsync(e => e.EventId == eventId));

    private static void AssertSame(DateTime expected, DateTime? actual) =>
        Assert.Equal(expected, actual!.Value, TimeSpan.FromSeconds(1));

    // ---------- вход и выход ----------

    [Fact]
    public async Task The_first_join_starts_the_lesson_and_opens_an_interval()
    {
        var lesson = await ScheduledLessonAsync();
        var at = Now;

        var eventId = await JoinedAsync(lesson, lesson.A, at);

        Assert.Equal(SessionStatus.InProgress, await StatusAsync(lesson.SessionId));
        var row = Assert.Single(await RowsAsync(lesson.SessionId));
        Assert.Equal(lesson.A.Id, row.UserId);
        AssertSame(at, row.JoinedAt);
        Assert.Null(row.LeftAt);
        Assert.NotNull((await EventAsync(eventId)).ProcessedAt);
        var state = await JsonAsync(await ApiAsync(lesson.B, HttpMethod.Get, $"/sessions/{lesson.SessionId}"));
        Assert.Equal("InProgress", state.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_leave_closes_the_interval_and_the_second_participant_gets_their_own()
    {
        var lesson = await ScheduledLessonAsync();
        var t = Now;

        await JoinedAsync(lesson, lesson.A, t);
        await JoinedAsync(lesson, lesson.B, t.AddMinutes(2));
        await LeftAsync(lesson, lesson.A, t.AddMinutes(10));

        var rows = await RowsAsync(lesson.SessionId);
        Assert.Equal(2, rows.Count);
        var a = rows.Single(r => r.UserId == lesson.A.Id);
        var b = rows.Single(r => r.UserId == lesson.B.Id);
        AssertSame(t.AddMinutes(10), a.LeftAt);
        Assert.Null(b.LeftAt);
        Assert.Equal(SessionStatus.InProgress, await StatusAsync(lesson.SessionId));
    }

    [Fact]
    public async Task Reconnects_make_several_intervals_for_one_participant()
    {
        var lesson = await ScheduledLessonAsync();
        var t = Now;

        await JoinedAsync(lesson, lesson.A, t);
        await LeftAsync(lesson, lesson.A, t.AddMinutes(5));
        await JoinedAsync(lesson, lesson.A, t.AddMinutes(7));
        await LeftAsync(lesson, lesson.A, t.AddMinutes(20));
        await JoinedAsync(lesson, lesson.A, t.AddMinutes(21));

        var rows = await RowsAsync(lesson.SessionId);
        Assert.Equal(3, rows.Count);
        AssertSame(t.AddMinutes(5), rows[0].LeftAt);
        AssertSame(t.AddMinutes(20), rows[1].LeftAt);
        Assert.Null(rows[2].LeftAt);
    }

    // ---------- повторы, порядок, сбои ----------

    [Fact]
    public async Task A_repeated_delivery_changes_nothing()
    {
        var lesson = await ScheduledLessonAsync();
        var t = Now;
        var id = LiveKitBodies.NewEventId();
        var body = LiveKitBodies.ParticipantJoined(id, lesson.Room, lesson.A.Id.ToString("D"), createdAt: Unix(t));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(body)).StatusCode);

        Assert.Single(await RowsAsync(lesson.SessionId));
        Assert.Equal(SessionStatus.InProgress, await StatusAsync(lesson.SessionId));
    }

    [Fact]
    public async Task A_different_event_with_the_same_join_moment_does_not_double_the_interval()
    {
        var lesson = await ScheduledLessonAsync();
        var t = Now;

        await JoinedAsync(lesson, lesson.A, t);
        await JoinedAsync(lesson, lesson.A, t.AddMinutes(1));   // повторный вход без выхода

        var row = Assert.Single(await RowsAsync(lesson.SessionId));
        AssertSame(t, row.JoinedAt);
    }

    [Fact]
    public async Task A_leave_that_arrives_before_its_join_still_gives_a_correct_interval()
    {
        var lesson = await ScheduledLessonAsync();
        var t = Now;

        await LeftAsync(lesson, lesson.A, t.AddMinutes(10));   // выход пришёл раньше входа
        Assert.Empty(await RowsAsync(lesson.SessionId));
        await JoinedAsync(lesson, lesson.A, t);

        var row = Assert.Single(await RowsAsync(lesson.SessionId));
        AssertSame(t, row.JoinedAt);
        AssertSame(t.AddMinutes(10), row.LeftAt);
    }

    [Fact]
    public async Task A_second_join_that_arrives_before_the_first_leave_is_not_lost()
    {
        var lesson = await ScheduledLessonAsync();
        var t = Now;

        await JoinedAsync(lesson, lesson.A, t);
        await JoinedAsync(lesson, lesson.A, t.AddMinutes(7));    // пришёл раньше выхода между ними
        await LeftAsync(lesson, lesson.A, t.AddMinutes(5));

        var rows = await RowsAsync(lesson.SessionId);
        Assert.Equal(2, rows.Count);
        AssertSame(t.AddMinutes(5), rows[0].LeftAt);
        AssertSame(t.AddMinutes(7), rows[1].JoinedAt);
        Assert.Null(rows[1].LeftAt);
    }

    [Fact]
    public async Task An_event_recorded_but_not_processed_is_processed_on_redelivery()
    {
        var lesson = await ScheduledLessonAsync();
        var t = Now;
        var id = LiveKitBodies.NewEventId();

        // Прошлая попытка записала событие и упала до учёта: ProcessedAt пусто, присутствия нет.
        await _factory.QueryAsync(async db =>
        {
            db.Add(LiveKitWebhookEvent.Create(id, "participant_joined", lesson.Room, lesson.A.Id.ToString("D"), t));
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.Empty(await RowsAsync(lesson.SessionId));
        Assert.Null((await EventAsync(id)).ProcessedAt);

        var redelivery = LiveKitBodies.ParticipantJoined(id, lesson.Room, lesson.A.Id.ToString("D"), createdAt: Unix(t));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(redelivery)).StatusCode);

        Assert.Single(await RowsAsync(lesson.SessionId));
        Assert.NotNull((await EventAsync(id)).ProcessedAt);
        Assert.Equal(SessionStatus.InProgress, await StatusAsync(lesson.SessionId));
    }

    [Fact]
    public async Task Both_participants_joining_at_the_same_moment_give_two_intervals_and_one_start()
    {
        var lesson = await ScheduledLessonAsync();
        var t = Now;

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => PostAsync(LiveKitBodies.ParticipantJoined(
                LiveKitBodies.NewEventId(), lesson.Room, (i % 2 == 0 ? lesson.A : lesson.B).Id.ToString("D"), createdAt: Unix(t) + i))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var rows = await RowsAsync(lesson.SessionId);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.UserId == lesson.A.Id);
        Assert.Contains(rows, r => r.UserId == lesson.B.Id);
        Assert.Equal(SessionStatus.InProgress, await StatusAsync(lesson.SessionId));
    }

    // ---------- что не должно учитываться ----------

    [Fact]
    public async Task Someone_else_in_the_room_is_not_counted()
    {
        var lesson = await ScheduledLessonAsync();
        var id = LiveKitBodies.NewEventId();

        var response = await PostAsync(LiveKitBodies.ParticipantJoined(id, lesson.Room, Guid.NewGuid().ToString("D"), createdAt: Unix(Now)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await RowsAsync(lesson.SessionId));
        Assert.Equal(SessionStatus.Scheduled, await StatusAsync(lesson.SessionId));
        Assert.NotNull((await EventAsync(id)).ProcessedAt);
    }

    [Theory]
    [InlineData("session-not-a-guid")]
    [InlineData("dev-room")]
    [InlineData("session-00000000-0000-0000-0000-000000000000")]
    public async Task Foreign_and_unknown_rooms_are_accepted_and_ignored(string room)
    {
        var id = LiveKitBodies.NewEventId();

        var response = await PostAsync(LiveKitBodies.ParticipantJoined(id, room, Guid.NewGuid().ToString("D"), createdAt: Unix(Now)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await EventAsync(id)).ProcessedAt);
    }

    [Fact]
    public async Task A_lesson_nobody_confirmed_does_not_start_from_a_join()
    {
        var lesson = await ScheduledLessonAsync(confirm: false);

        await JoinedAsync(lesson, lesson.A, Now);

        Assert.Empty(await RowsAsync(lesson.SessionId));
        Assert.Equal(SessionStatus.Proposed, await StatusAsync(lesson.SessionId));
    }

    [Theory]
    [InlineData(SessionStatus.Completed)]
    [InlineData(SessionStatus.Unconfirmed)]
    [InlineData(SessionStatus.Cancelled)]
    public async Task A_closed_lesson_does_not_change_from_late_events(SessionStatus status)
    {
        var lesson = await ScheduledLessonAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Sessions.Where(s => s.Id == lesson.SessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status));
            return 0;
        });

        await JoinedAsync(lesson, lesson.A, Now);

        Assert.Empty(await RowsAsync(lesson.SessionId));
        Assert.Equal(status, await StatusAsync(lesson.SessionId));
    }

    [Fact]
    public async Task A_late_joiner_does_not_change_the_status_of_a_lesson_already_marked_no_show()
    {
        var lesson = await ScheduledLessonAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Sessions.Where(s => s.Id == lesson.SessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.NoShow));
            return 0;
        });

        await JoinedAsync(lesson, lesson.B, Now);

        Assert.Single(await RowsAsync(lesson.SessionId));   // присутствие считается: итог подведёт закрытие комнаты
        Assert.Equal(SessionStatus.NoShow, await StatusAsync(lesson.SessionId));
    }

    // ---------- закрытие комнаты ----------

    [Fact]
    public async Task A_finished_room_closes_open_intervals_of_both()
    {
        var lesson = await ScheduledLessonAsync();
        var t = Now;
        await JoinedAsync(lesson, lesson.A, t);
        await JoinedAsync(lesson, lesson.B, t.AddMinutes(1));

        var finished = LiveKitBodies.RoomFinished(LiveKitBodies.NewEventId(), lesson.Room, Unix(t.AddMinutes(30)));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(finished)).StatusCode);

        var rows = await RowsAsync(lesson.SessionId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => AssertSame(t.AddMinutes(30), r.LeftAt));
    }
}
