using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>
/// Автоматическая фиксация урока (раздел 6.3 ТЗ): проверка на 15-й минуте и итог при закрытии комнаты (старт + 75 минут) по присутствию.
/// Время приложения заморожено, поэтому урок «сдвигается» в прошлое так, чтобы нужный момент уже наступил.
/// </summary>
[Collection(ApiCollection.Name)]
public class VideoLessonJobsTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public VideoLessonJobsTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
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

    /// <summary>Подтверждённый урок (преподаёт A), начавшийся <paramref name="startedMinutesAgo"/> минут назад; статус как у урока, в который уже входили.</summary>
    private async Task<Lesson> LessonAsync(int startedMinutesAgo, SessionStatus status = SessionStatus.InProgress)
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

        var oldTicks = Now.AddHours(5).Ticks;
        var start = Now.AddMinutes(-startedMinutesAgo);
        await _factory.QueryAsync(async db =>
        {
            await db.Sessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.StartAt, start).SetProperty(x => x.EndAt, start.AddMinutes(45)).SetProperty(x => x.Status, status));
            return 0;
        });
        _ = oldTicks;
        var ticks = (await LoadAsync(sessionId)).StartAt.Ticks;
        return new Lesson(a, b, exchangeId, sessionId, new DateTime(ticks, DateTimeKind.Utc), ticks);
    }

    private Task<LessonSession> LoadAsync(Guid id) => _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == id));

    private Task SeedAsync(Lesson lesson, MatchPerson who, int fromMin, int? toMin) => _factory.QueryAsync(async db =>
    {
        db.Add(SessionAttendance.Restore(lesson.SessionId, who.Id, lesson.Start.AddMinutes(fromMin), toMin is null ? null : lesson.Start.AddMinutes(toMin.Value)));
        await db.SaveChangesAsync();
        return 0;
    });

    private async Task RunAsync(Func<VideoLessonJobs, Task> job)
    {
        using var scope = _factory.Services.CreateScope();
        await job(scope.ServiceProvider.GetRequiredService<VideoLessonJobs>());
    }

    private async Task<List<JsonElement>> NotificationsOf(MatchPerson who, string type)
    {
        var list = (await JsonAsync(await ApiAsync(who, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return list.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    // ---------- проверка на 15-й минуте ----------

    [Fact]
    public async Task Nobody_in_the_room_at_the_fifteenth_minute_is_missed_and_both_are_told()
    {
        var lesson = await LessonAsync(startedMinutesAgo: 16, SessionStatus.Scheduled);

        await RunAsync(j => j.FirstCheckAsync(lesson.SessionId, lesson.Ticks));

        var s = await LoadAsync(lesson.SessionId);
        Assert.Equal(SessionStatus.Missed, s.Status);
        Assert.Null(s.NoShowUserId);
        Assert.Single(await NotificationsOf(lesson.A, "SessionMissed"));
        Assert.Single(await NotificationsOf(lesson.B, "SessionMissed"));
    }

    [Fact]
    public async Task One_in_the_room_at_the_fifteenth_minute_makes_the_absent_one_the_no_show()
    {
        var lesson = await LessonAsync(16);
        await SeedAsync(lesson, lesson.A, 1, null);

        await RunAsync(j => j.FirstCheckAsync(lesson.SessionId, lesson.Ticks));

        var s = await LoadAsync(lesson.SessionId);
        Assert.Equal(SessionStatus.NoShow, s.Status);
        Assert.Equal(lesson.B.Id, s.NoShowUserId);
        foreach (var who in new[] { lesson.A, lesson.B })
        {
            var n = Assert.Single(await NotificationsOf(who, "SessionNoShow"));
            Assert.Equal(lesson.B.Id, n.GetProperty("payload").GetProperty("userId").GetGuid());   // в уведомлении виновник — не пришедший
        }
    }

    [Fact]
    public async Task Both_in_the_room_at_the_fifteenth_minute_leave_the_lesson_in_progress_quietly()
    {
        var lesson = await LessonAsync(16);
        await SeedAsync(lesson, lesson.A, 0, null);
        await SeedAsync(lesson, lesson.B, 14, null);

        await RunAsync(j => j.FirstCheckAsync(lesson.SessionId, lesson.Ticks));

        Assert.Equal(SessionStatus.InProgress, (await LoadAsync(lesson.SessionId)).Status);
        Assert.Empty(await NotificationsOf(lesson.A, "SessionNoShow"));
        Assert.Empty(await NotificationsOf(lesson.A, "SessionMissed"));
    }

    [Fact]
    public async Task A_join_after_the_fifteenth_minute_is_too_late_for_the_first_check()
    {
        var lesson = await LessonAsync(20);
        await SeedAsync(lesson, lesson.A, 1, null);
        await SeedAsync(lesson, lesson.B, 16, null);

        await RunAsync(j => j.FirstCheckAsync(lesson.SessionId, lesson.Ticks));

        var s = await LoadAsync(lesson.SessionId);
        Assert.Equal(SessionStatus.NoShow, s.Status);
        Assert.Equal(lesson.B.Id, s.NoShowUserId);
    }

    [Fact]
    public async Task Time_spent_in_the_room_only_before_the_start_does_not_count_as_being_there()
    {
        var lesson = await LessonAsync(16, SessionStatus.Scheduled);
        await SeedAsync(lesson, lesson.A, -8, -2);

        await RunAsync(j => j.FirstCheckAsync(lesson.SessionId, lesson.Ticks));

        Assert.Equal(SessionStatus.Missed, (await LoadAsync(lesson.SessionId)).Status);
    }

    [Fact]
    public async Task The_first_check_is_idempotent_early_runs_and_stale_runs_do_nothing()
    {
        var early = await LessonAsync(10);
        await RunAsync(j => j.FirstCheckAsync(early.SessionId, early.Ticks));
        Assert.Equal(SessionStatus.InProgress, (await LoadAsync(early.SessionId)).Status);

        var lesson = await LessonAsync(16, SessionStatus.Scheduled);
        await RunAsync(j => j.FirstCheckAsync(lesson.SessionId, lesson.Ticks - 1));   // задача на прежнее время (урок перенесли)
        Assert.Equal(SessionStatus.Scheduled, (await LoadAsync(lesson.SessionId)).Status);

        await RunAsync(j => j.FirstCheckAsync(lesson.SessionId, lesson.Ticks));
        await RunAsync(j => j.FirstCheckAsync(lesson.SessionId, lesson.Ticks));
        Assert.Single(await NotificationsOf(lesson.A, "SessionMissed"));
    }

    // ---------- закрытие комнаты ----------

    [Fact]
    public async Task Thirty_joint_minutes_complete_the_lesson_it_counts_in_the_balance_and_the_room_is_deleted()
    {
        var lesson = await LessonAsync(80);
        await SeedAsync(lesson, lesson.A, 0, 40);
        await SeedAsync(lesson, lesson.B, 10, null);   // вышел в плановое время вместе с закрытием: интервал открыт

        await RunAsync(j => j.CloseRoomAsync(lesson.SessionId, lesson.Ticks));

        var s = await LoadAsync(lesson.SessionId);
        Assert.Equal(SessionStatus.Completed, s.Status);
        Assert.Equal(10, s.LateJoinMinutes);
        Assert.Single(await NotificationsOf(lesson.A, "SessionCompleted"));
        Assert.Single(await NotificationsOf(lesson.B, "SessionCompleted"));
        Assert.Contains(lesson.Room, _factory.Rooms.Deleted);

        var rows = await _factory.QueryAsync(db => db.SessionAttendances.AsNoTracking().Where(a => a.SessionId == lesson.SessionId).ToListAsync());
        Assert.Equal(lesson.Start.AddMinutes(40), rows.Single(r => r.UserId == lesson.A.Id).LeftAt);    // закрытый остаётся как был
        Assert.Equal(lesson.Start.AddMinutes(75), rows.Single(r => r.UserId == lesson.B.Id).LeftAt);    // открытый закрыт закрытием комнаты

        var balance = (await JsonAsync(await ApiAsync(lesson.A, HttpMethod.Get, $"/exchanges/{lesson.ExchangeId}"))).GetProperty("balance");
        Assert.Equal(1, balance.GetProperty("myLessons").GetInt32());       // преподавал A
        Assert.Equal(0, balance.GetProperty("partnerLessons").GetInt32());
    }

    [Fact]
    public async Task Exactly_thirty_and_twenty_nine_joint_minutes_are_the_border()
    {
        var thirty = await LessonAsync(80);
        await SeedAsync(thirty, thirty.A, 0, 30);
        await SeedAsync(thirty, thirty.B, 0, 30);
        var twentyNine = await LessonAsync(80);
        await SeedAsync(twentyNine, twentyNine.A, 0, 29);
        await SeedAsync(twentyNine, twentyNine.B, 0, 29);

        await RunAsync(j => j.CloseRoomAsync(thirty.SessionId, thirty.Ticks));
        await RunAsync(j => j.CloseRoomAsync(twentyNine.SessionId, twentyNine.Ticks));

        Assert.Equal(SessionStatus.Completed, (await LoadAsync(thirty.SessionId)).Status);
        Assert.Equal(SessionStatus.Unconfirmed, (await LoadAsync(twentyNine.SessionId)).Status);
    }

    [Fact]
    public async Task Minutes_before_the_planned_start_do_not_count_as_joint_time()
    {
        var lesson = await LessonAsync(80);
        await SeedAsync(lesson, lesson.A, -10, 25);   // вместе 25 минут урока и 10 минут до него: засчитывается только 25
        await SeedAsync(lesson, lesson.B, -10, 25);

        await RunAsync(j => j.CloseRoomAsync(lesson.SessionId, lesson.Ticks));

        Assert.Equal(SessionStatus.Unconfirmed, (await LoadAsync(lesson.SessionId)).Status);
    }

    [Fact]
    public async Task Reconnects_add_up_to_the_joint_time()
    {
        var lesson = await LessonAsync(80);
        await SeedAsync(lesson, lesson.A, 0, 20);
        await SeedAsync(lesson, lesson.A, 25, 60);
        await SeedAsync(lesson, lesson.B, 10, 30);
        await SeedAsync(lesson, lesson.B, 40, 70);   // вместе: 10–20, 25–30, 40–60 = 35

        await RunAsync(j => j.CloseRoomAsync(lesson.SessionId, lesson.Ticks));

        Assert.Equal(SessionStatus.Completed, (await LoadAsync(lesson.SessionId)).Status);
    }

    [Fact]
    public async Task A_late_partner_turns_no_show_into_completed_and_the_lateness_is_kept()
    {
        var lesson = await LessonAsync(80, SessionStatus.NoShow);
        await _factory.QueryAsync(async db =>
        {
            await db.Sessions.Where(s => s.Id == lesson.SessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.NoShowUserId, lesson.B.Id));
            return 0;
        });
        await SeedAsync(lesson, lesson.A, 0, null);
        await SeedAsync(lesson, lesson.B, 20, null);

        await RunAsync(j => j.CloseRoomAsync(lesson.SessionId, lesson.Ticks));

        var s = await LoadAsync(lesson.SessionId);
        Assert.Equal(SessionStatus.Completed, s.Status);
        Assert.Equal(20, s.LateJoinMinutes);
        Assert.Equal(lesson.B.Id, s.NoShowUserId);
        Assert.Single(await NotificationsOf(lesson.A, "SessionCompleted"));
    }

    [Fact]
    public async Task Both_there_but_less_than_thirty_minutes_is_unconfirmed_with_24_hours_from_the_closing_for_a_manual_answer()
    {
        var lesson = await LessonAsync(80);
        await SeedAsync(lesson, lesson.A, 0, 20);
        await SeedAsync(lesson, lesson.B, 5, 20);
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<IBackgroundJobs>();
        var before = jobs.Scheduled.Count;

        await RunAsync(j => j.CloseRoomAsync(lesson.SessionId, lesson.Ticks));

        var s = await LoadAsync(lesson.SessionId);
        Assert.Equal(SessionStatus.Unconfirmed, s.Status);
        Assert.Equal(Now, s.UnconfirmedAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Single(await NotificationsOf(lesson.A, "SessionNeedsConfirmation"));
        Assert.Single(await NotificationsOf(lesson.B, "SessionNeedsConfirmation"));
        Assert.Contains(jobs.Scheduled.Skip(before), j => j.Description.Contains(nameof(LessonSessionJobs.ExpireUnconfirmedAsync)));

        Assert.Equal(HttpStatusCode.OK, (await ApiAsync(lesson.A, HttpMethod.Post, $"/sessions/{lesson.SessionId}/confirm-manual")).StatusCode);
        var done = await JsonAsync(await ApiAsync(lesson.B, HttpMethod.Post, $"/sessions/{lesson.SessionId}/confirm-manual"));
        Assert.Equal("Completed", done.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_lone_participant_makes_a_no_show_and_nobody_makes_a_missed_when_the_first_check_did_not_run()
    {
        var lone = await LessonAsync(80);
        await SeedAsync(lone, lone.B, 1, 60);
        var nobody = await LessonAsync(80, SessionStatus.Scheduled);

        await RunAsync(j => j.CloseRoomAsync(lone.SessionId, lone.Ticks));
        await RunAsync(j => j.CloseRoomAsync(nobody.SessionId, nobody.Ticks));

        var s = await LoadAsync(lone.SessionId);
        Assert.Equal(SessionStatus.NoShow, s.Status);
        Assert.Equal(lone.A.Id, s.NoShowUserId);
        Assert.Single(await NotificationsOf(lone.B, "SessionNoShow"));
        Assert.Equal(SessionStatus.Missed, (await LoadAsync(nobody.SessionId)).Status);
        Assert.Single(await NotificationsOf(nobody.A, "SessionMissed"));
    }

    [Fact]
    public async Task A_no_show_already_announced_at_the_fifteenth_minute_is_not_announced_again_at_the_closing()
    {
        var lesson = await LessonAsync(80);
        await SeedAsync(lesson, lesson.A, 1, 60);
        await _factory.QueryAsync(async db =>
        {
            await db.Sessions.Where(s => s.Id == lesson.SessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.NoShow).SetProperty(x => x.NoShowUserId, lesson.B.Id));
            return 0;
        });
        var announced = (await NotificationsOf(lesson.A, "SessionNoShow")).Count;

        await RunAsync(j => j.CloseRoomAsync(lesson.SessionId, lesson.Ticks));

        Assert.Equal(SessionStatus.NoShow, (await LoadAsync(lesson.SessionId)).Status);   // итог окончательный
        Assert.Equal(announced, (await NotificationsOf(lesson.A, "SessionNoShow")).Count);
        Assert.Contains(lesson.Room, _factory.Rooms.Deleted);
    }

    // ---------- сбои и повторы ----------

    [Fact]
    public async Task Events_recorded_but_not_processed_are_counted_before_the_outcome()
    {
        var lesson = await LessonAsync(80);
        await _factory.QueryAsync(async db =>
        {
            foreach (var who in new[] { lesson.A, lesson.B })
            {
                db.Add(LiveKitWebhookEvent.Create(LiveKitIds.New(), "participant_joined", lesson.Room, who.Id.ToString("D"), lesson.Start.AddMinutes(1)));
            }

            await db.SaveChangesAsync();
            return 0;
        });

        await RunAsync(j => j.CloseRoomAsync(lesson.SessionId, lesson.Ticks));

        Assert.Equal(SessionStatus.Completed, (await LoadAsync(lesson.SessionId)).Status);   // 74 минуты вместе
        var unprocessed = await _factory.QueryAsync(db => db.Set<LiveKitWebhookEvent>().CountAsync(e => e.RoomName == lesson.Room && e.ProcessedAt == null));
        Assert.Equal(0, unprocessed);
    }

    [Fact]
    public async Task When_livekit_is_down_the_outcome_is_kept_and_a_retry_only_deletes_the_room()
    {
        var lesson = await LessonAsync(80);
        await SeedAsync(lesson, lesson.A, 0, 50);
        await SeedAsync(lesson, lesson.B, 0, 50);

        _factory.Rooms.Fail = true;
        try
        {
            await Assert.ThrowsAsync<LiveKitUnavailableException>(() => RunAsync(j => j.CloseRoomAsync(lesson.SessionId, lesson.Ticks)));
        }
        finally
        {
            _factory.Rooms.Fail = false;
        }

        Assert.Equal(SessionStatus.Completed, (await LoadAsync(lesson.SessionId)).Status);
        Assert.DoesNotContain(lesson.Room, _factory.Rooms.Deleted);

        await RunAsync(j => j.CloseRoomAsync(lesson.SessionId, lesson.Ticks));   // повтор Hangfire

        Assert.Contains(lesson.Room, _factory.Rooms.Deleted);
        Assert.Single(await NotificationsOf(lesson.A, "SessionCompleted"));
        Assert.Equal(SessionStatus.Completed, (await LoadAsync(lesson.SessionId)).Status);
    }

    [Fact]
    public async Task Running_before_the_closing_time_or_for_a_moved_lesson_changes_nothing_and_keeps_the_room()
    {
        var early = await LessonAsync(60);
        await SeedAsync(early, early.A, 0, null);
        await SeedAsync(early, early.B, 0, null);
        var moved = await LessonAsync(80);

        await RunAsync(j => j.CloseRoomAsync(early.SessionId, early.Ticks));
        await RunAsync(j => j.CloseRoomAsync(moved.SessionId, moved.Ticks - 1));

        Assert.Equal(SessionStatus.InProgress, (await LoadAsync(early.SessionId)).Status);
        Assert.Equal(SessionStatus.InProgress, (await LoadAsync(moved.SessionId)).Status);
        Assert.DoesNotContain(early.Room, _factory.Rooms.Deleted);
        Assert.DoesNotContain(moved.Room, _factory.Rooms.Deleted);
    }

    [Fact]
    public async Task A_cancelled_lesson_has_no_room_to_delete()
    {
        var lesson = await LessonAsync(80, SessionStatus.Cancelled);

        await RunAsync(j => j.CloseRoomAsync(lesson.SessionId, lesson.Ticks));

        Assert.Equal(SessionStatus.Cancelled, (await LoadAsync(lesson.SessionId)).Status);
        Assert.DoesNotContain(lesson.Room, _factory.Rooms.Deleted);
    }

    [Fact]
    public async Task Without_livekit_settings_the_outcome_is_still_decided_and_nothing_is_deleted()
    {
        var lesson = await LessonAsync(80);
        await SeedAsync(lesson, lesson.A, 0, 50);
        await SeedAsync(lesson, lesson.B, 0, 50);

        _factory.Rooms.IsConfigured = false;
        try
        {
            await RunAsync(j => j.CloseRoomAsync(lesson.SessionId, lesson.Ticks));
        }
        finally
        {
            _factory.Rooms.IsConfigured = true;
        }

        Assert.Equal(SessionStatus.Completed, (await LoadAsync(lesson.SessionId)).Status);
        Assert.DoesNotContain(lesson.Room, _factory.Rooms.Deleted);
    }

    private static class LiveKitIds
    {
        public static string New() => "EV_" + Guid.NewGuid().ToString("N")[..12];
    }
}
