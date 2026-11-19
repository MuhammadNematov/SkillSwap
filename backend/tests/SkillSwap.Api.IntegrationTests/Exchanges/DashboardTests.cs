using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>Главная страница кабинета: GET /me/dashboard.</summary>
[Collection(ApiCollection.Name)]
public class DashboardTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public DashboardTests(ApiFactory factory)
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

    private async Task<JsonElement> DashboardAsync(MatchPerson who)
    {
        var response = await SendAsync(who, HttpMethod.Get, "/me/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private async Task<Guid> OpenExchangeAsync(MatchTestWorld world, MatchPerson a, MatchPerson b)
    {
        var created = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        return (await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept"))).GetProperty("exchangeId").GetGuid();
    }

    private async Task<Guid> ScheduledAsync(MatchPerson proposer, MatchPerson answerer, Guid exchangeId, DateTime start, Guid? teacher = null)
    {
        var created = await SendAsync(proposer, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { startAt = start.ToString("O"), teacherId = teacher ?? proposer.Id, topic = "Тема" });
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(answerer, HttpMethod.Post, $"/sessions/{id}/confirm")).StatusCode);
        return id;
    }

    private Task SetSessionAsync(Guid id, SessionStatus status, DateTime? start = null, DateTime? unconfirmedAt = null) => _factory.QueryAsync(async db =>
    {
        await db.Sessions.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status));
        if (start is { } at)
        {
            await db.Sessions.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartAt, at).SetProperty(x => x.EndAt, at.AddMinutes(45)));
        }

        if (unconfirmedAt is { } u)
        {
            await db.Sessions.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UnconfirmedAt, u));
        }

        return 0;
    });

    private static List<JsonElement> Actions(JsonElement dashboard, string type) =>
        dashboard.GetProperty("actionsNeeded").EnumerateArray().Where(a => a.GetProperty("type").GetString() == type).ToList();

    // ---------- пустая главная ----------

    [Fact]
    public async Task A_newcomer_without_anything_sees_zeros_and_no_next_lesson()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var person = await world.UserAsync(Beginner0, Beginner1);

        var dashboard = await DashboardAsync(person);

        Assert.Equal(JsonValueKind.Null, dashboard.GetProperty("nextSession").ValueKind);
        var counters = dashboard.GetProperty("counters");
        foreach (var name in new[] { "activeExchanges", "incomingRequests", "outgoingRequests", "unreadMessages", "unreadNotifications" })
        {
            Assert.Equal(0, counters.GetProperty(name).GetInt32());
        }

        var stats = dashboard.GetProperty("stats");
        foreach (var name in new[] { "completedSessions", "taughtMinutes", "learnedMinutes", "completedExchanges" })
        {
            Assert.Equal(0, stats.GetProperty(name).GetInt32());
        }

        Assert.True(dashboard.GetProperty("rating").GetProperty("isNewcomer").GetBoolean());
        Assert.Equal(JsonValueKind.Null, dashboard.GetProperty("rating").GetProperty("average").ValueKind);
        Assert.Equal(0, dashboard.GetProperty("actionsNeeded").GetArrayLength());
    }

    [Fact]
    public async Task Anonymous_staff_and_deleted_accounts_are_refused()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var person = await world.UserAsync(Beginner0, Beginner1);
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Moderator);
        await world.ModifyAsync(db => { db.Users.Add(staff); return Task.CompletedTask; });

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/me/dashboard")).StatusCode);
        var staffResponse = await SendAsync(new MatchPerson(staff, TestTokens.ForUser(_factory, staff)), HttpMethod.Get, "/me/dashboard");
        Assert.Equal(HttpStatusCode.Forbidden, staffResponse.StatusCode);

        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == person.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Deleted));
            return 0;
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(person, HttpMethod.Get, "/me/dashboard")).StatusCode);
    }

    // ---------- счётчики ----------

    [Fact]
    public async Task Counters_follow_requests_exchanges_messages_and_notifications_for_each_side()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var c = await world.UserAsync(Beginner1, Beginner0);
        var d = await world.UserAsync(Beginner1, Beginner0);
        var withB = await OpenExchangeAsync(world, a, b);                       // активный обмен
        await SendAsync(a, HttpMethod.Post, "/exchange-requests", new { toUserId = c.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });   // исходящая
        await SendAsync(d, HttpMethod.Post, "/exchange-requests", new { toUserId = a.Id, toTeachesSkillId = world.S(0), fromTeachesSkillId = world.S(1), durationDays = 30 });   // входящая
        await _factory.QueryAsync(async db =>
        {
            var cipher = _factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IMessageCipher>();
            db.Messages.Add(Message.CreateText(withB, b.Id, cipher.Encrypt("раз"), false));
            db.Messages.Add(Message.CreateText(withB, b.Id, cipher.Encrypt("два"), false));
            db.Messages.Add(Message.CreateText(withB, a.Id, cipher.Encrypt("мой"), false));   // свои не считаются
            await db.SaveChangesAsync();
            return 0;
        });

        var counters = (await DashboardAsync(a)).GetProperty("counters");

        Assert.Equal(1, counters.GetProperty("activeExchanges").GetInt32());
        Assert.Equal(1, counters.GetProperty("incomingRequests").GetInt32());
        Assert.Equal(1, counters.GetProperty("outgoingRequests").GetInt32());
        Assert.Equal(2, counters.GetProperty("unreadMessages").GetInt32());
        Assert.True(counters.GetProperty("unreadNotifications").GetInt32() >= 1);   // заявка d

        var theirs = (await DashboardAsync(b)).GetProperty("counters");
        Assert.Equal(1, theirs.GetProperty("unreadMessages").GetInt32());   // у партнёра непрочитано только моё сообщение
        Assert.Equal(0, theirs.GetProperty("incomingRequests").GetInt32());

        await SendAsync(a, HttpMethod.Post, $"/exchanges/{withB}/messages/read");
        Assert.Equal(0, (await DashboardAsync(a)).GetProperty("counters").GetProperty("unreadMessages").GetInt32());
    }

    [Fact]
    public async Task Expired_requests_deleted_messages_and_archived_chats_do_not_count()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var c = await world.UserAsync(Beginner1, Beginner0);
        var withB = await OpenExchangeAsync(world, a, b);
        var withC = await OpenExchangeAsync(world, a, c);
        var d = await world.UserAsync(Beginner1, Beginner0);
        var stale = (await JsonAsync(await SendAsync(d, HttpMethod.Post, "/exchange-requests",
            new { toUserId = a.Id, toTeachesSkillId = world.S(0), fromTeachesSkillId = world.S(1), durationDays = 30 }))).GetProperty("id").GetGuid();
        await _factory.QueryAsync(async db =>
        {
            var cipher = _factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IMessageCipher>();
            await db.ExchangeRequests.Where(r => r.Id == stale).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, Now.AddMinutes(-1)));
            var deleted = Message.CreateText(withB, b.Id, cipher.Encrypt("удалено"), false);
            db.Messages.Add(deleted);
            db.Messages.Add(Message.CreateText(withC, c.Id, cipher.Encrypt("в архивном чате"), false));
            await db.SaveChangesAsync();
            await db.Messages.Where(m => m.Id == deleted.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, Now));
            await db.Exchanges.Where(e => e.Id == withC).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExchangeStatus.Completed).SetProperty(e => e.ClosedAt, Now.AddDays(-40)));
            return 0;
        });

        var dashboard = await DashboardAsync(a);

        Assert.Equal(0, dashboard.GetProperty("counters").GetProperty("incomingRequests").GetInt32());
        Assert.Equal(0, dashboard.GetProperty("counters").GetProperty("unreadMessages").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("counters").GetProperty("activeExchanges").GetInt32());   // завершённый обмен не активен
        Assert.Empty(Actions(dashboard, "AnswerRequest"));
    }

    // ---------- ближайший урок ----------

    [Fact]
    public async Task The_next_lesson_is_the_nearest_confirmed_one_with_the_partner_and_the_join_window()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var c = await world.UserAsync(Beginner1, Beginner0);
        var withB = await OpenExchangeAsync(world, a, b);
        var withC = await OpenExchangeAsync(world, a, c);
        var farther = await ScheduledAsync(a, b, withB, Now.AddDays(5));
        var nearest = await ScheduledAsync(c, a, withC, Now.AddDays(2), teacher: a.Id);   // преподаю я, предложил C
        await SendAsync(a, HttpMethod.Post, $"/exchanges/{withB}/sessions", new { startAt = Now.AddDays(1).ToString("O"), teacherId = a.Id });   // ближе, но не подтверждено
        var cancelled = await ScheduledAsync(a, b, withB, Now.AddDays(1).AddHours(5));
        await SendAsync(a, HttpMethod.Post, $"/sessions/{cancelled}/cancel");

        var dashboard = await DashboardAsync(a);

        var next = dashboard.GetProperty("nextSession");
        Assert.Equal(nearest, next.GetProperty("id").GetGuid());
        Assert.Equal(withC, next.GetProperty("exchangeId").GetGuid());
        Assert.Equal("Scheduled", next.GetProperty("status").GetString());
        Assert.True(next.GetProperty("iAmTeacher").GetBoolean());
        Assert.Equal("Тема", next.GetProperty("topic").GetString());
        Assert.Equal(45, next.GetProperty("durationMin").GetInt32());
        Assert.Equal(c.Id, next.GetProperty("partner").GetProperty("id").GetGuid());
        Assert.Equal(c.User.Name, next.GetProperty("partner").GetProperty("name").GetString());
        var start = next.GetProperty("startAt").GetDateTime();
        Assert.Equal(start.AddMinutes(-10), next.GetProperty("canJoinFrom").GetDateTime());                // вход за 10 минут до начала
        Assert.Equal(start.AddMinutes(45 + 30), next.GetProperty("canJoinUntil").GetDateTime());           // и 30 минут после окончания

        var partnerView = (await DashboardAsync(c)).GetProperty("nextSession");
        Assert.Equal(nearest, partnerView.GetProperty("id").GetGuid());
        Assert.False(partnerView.GetProperty("iAmTeacher").GetBoolean());   // у партнёра зеркально
        Assert.Equal(a.Id, partnerView.GetProperty("partner").GetProperty("id").GetGuid());
        Assert.NotEqual(farther, nearest);
    }

    [Fact]
    public async Task A_lesson_in_progress_stays_the_next_one_until_it_ends_and_then_gives_way()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var exchangeId = await OpenExchangeAsync(world, a, b);
        var now = await ScheduledAsync(a, b, exchangeId, Now.AddDays(2));
        var later = await ScheduledAsync(a, b, exchangeId, Now.AddDays(4));

        await SetSessionAsync(now, SessionStatus.Scheduled, start: Now.AddMinutes(-20));   // идёт сейчас (начался 20 минут назад)
        Assert.Equal(now, (await DashboardAsync(a)).GetProperty("nextSession").GetProperty("id").GetGuid());

        await SetSessionAsync(now, SessionStatus.Scheduled, start: Now.AddMinutes(-60));   // уже закончился
        Assert.Equal(later, (await DashboardAsync(a)).GetProperty("nextSession").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_partner_avatar_is_shown_only_when_the_profile_may_be_seen()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var exchangeId = await OpenExchangeAsync(world, a, b);
        await ScheduledAsync(a, b, exchangeId, Now.AddDays(2));
        var form = new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()), "file", "a.png" } };
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/avatar") { Content = form };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", b.Token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(put)).StatusCode);

        var shown = (await DashboardAsync(a)).GetProperty("nextSession").GetProperty("partner").GetProperty("avatarUrl").GetString();
        Assert.Contains("X-Amz-Signature=", shown);

        await SendAsync(b, HttpMethod.Post, $"/users/{a.Id}/block");   // обмен прерван, уроков больше нет
        Assert.Equal(JsonValueKind.Null, (await DashboardAsync(a)).GetProperty("nextSession").ValueKind);
    }

    // ---------- статистика ----------

    [Fact]
    public async Task Stats_count_only_completed_lessons_split_by_who_taught()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var exchangeId = await OpenExchangeAsync(world, a, b);
        var taught1 = await ScheduledAsync(a, b, exchangeId, Now.AddDays(2));                       // преподаю я
        var taught2 = await ScheduledAsync(a, b, exchangeId, Now.AddDays(3));
        var learned = await ScheduledAsync(b, a, exchangeId, Now.AddDays(4), teacher: b.Id);       // преподаёт партнёр
        var notCounted = await ScheduledAsync(a, b, exchangeId, Now.AddDays(5));
        var open = await ScheduledAsync(a, b, exchangeId, Now.AddDays(6));
        foreach (var id in new[] { taught1, taught2, learned })
        {
            await SetSessionAsync(id, SessionStatus.Completed);
        }

        await SetSessionAsync(notCounted, SessionStatus.NotCounted);
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExchangeStatus.Completed));
            return 0;
        });

        var stats = (await DashboardAsync(a)).GetProperty("stats");
        Assert.Equal(3, stats.GetProperty("completedSessions").GetInt32());
        Assert.Equal(90, stats.GetProperty("taughtMinutes").GetInt32());     // 2 × 45
        Assert.Equal(45, stats.GetProperty("learnedMinutes").GetInt32());
        Assert.Equal(1, stats.GetProperty("completedExchanges").GetInt32());

        var partnerStats = (await DashboardAsync(b)).GetProperty("stats");
        Assert.Equal(45, partnerStats.GetProperty("taughtMinutes").GetInt32());   // у партнёра зеркально
        Assert.Equal(90, partnerStats.GetProperty("learnedMinutes").GetInt32());
        Assert.NotEqual(open, notCounted);
    }

    [Fact]
    public async Task A_cancelled_or_aborted_exchange_is_not_a_completed_one()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var exchangeId = await OpenExchangeAsync(world, a, b);
        await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/abort", new { reason = "x" });

        var dashboard = await DashboardAsync(a);

        Assert.Equal(0, dashboard.GetProperty("stats").GetProperty("completedExchanges").GetInt32());
        Assert.Equal(0, dashboard.GetProperty("counters").GetProperty("activeExchanges").GetInt32());
    }

    // ---------- рейтинг ----------

    [Fact]
    public async Task The_rating_is_hidden_for_a_newcomer_and_shown_after_enough_reviews()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var rated = await world.UserAsync(Beginner0, Beginner1, new MatchTestWorld.Options(RatingCount: User.NewcomerReviewThreshold, Rating: 4.8m));
        var newcomer = await world.UserAsync(Beginner0, Beginner1, new MatchTestWorld.Options(RatingCount: 1, Rating: 5m));

        var shown = (await DashboardAsync(rated)).GetProperty("rating");
        Assert.False(shown.GetProperty("isNewcomer").GetBoolean());
        Assert.Equal(4.8m, shown.GetProperty("average").GetDecimal());
        Assert.Equal(User.NewcomerReviewThreshold, shown.GetProperty("count").GetInt32());

        var hidden = (await DashboardAsync(newcomer)).GetProperty("rating");
        Assert.True(hidden.GetProperty("isNewcomer").GetBoolean());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("average").ValueKind);   // два отзыва ничего не говорят о человеке
        Assert.Equal(1, hidden.GetProperty("count").GetInt32());
    }

    // ---------- что ждёт ответа ----------

    [Fact]
    public async Task Actions_list_what_waits_for_the_person_most_urgent_first()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var c = await world.UserAsync(Beginner1, Beginner0);
        var d = await world.UserAsync(Beginner1, Beginner0);
        var withB = await OpenExchangeAsync(world, a, b);
        var withC = await OpenExchangeAsync(world, a, c);

        // 1. Входящая заявка.
        var requestId = (await JsonAsync(await SendAsync(d, HttpMethod.Post, "/exchange-requests",
            new { toUserId = a.Id, toTeachesSkillId = world.S(0), fromTeachesSkillId = world.S(1), durationDays = 30 }))).GetProperty("id").GetGuid();
        // 2. Партнёр предложил время, жду ответа я.
        var proposed = (await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/exchanges/{withB}/sessions", new { startAt = Now.AddDays(2).ToString("O"), teacherId = b.Id }))).GetProperty("id").GetGuid();
        // 3. Урок закончился, я ещё не подтвердил.
        var unconfirmed = await ScheduledAsync(a, b, withB, Now.AddDays(3));
        await SetSessionAsync(unconfirmed, SessionStatus.Unconfirmed, start: Now.AddHours(-3), unconfirmedAt: Now.AddHours(-2));
        // 4. Партнёр нажал «Завершить», 5. другой партнёр предложил продлить.
        await SendAsync(c, HttpMethod.Post, $"/exchanges/{withC}/complete");
        await SendAsync(c, HttpMethod.Post, $"/exchanges/{withC}/extend");

        var dashboard = await DashboardAsync(a);

        var answerRequest = Assert.Single(Actions(dashboard, "AnswerRequest"));
        Assert.Equal(requestId, answerRequest.GetProperty("requestId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, answerRequest.GetProperty("dueAt").ValueKind);
        var answerSession = Assert.Single(Actions(dashboard, "AnswerSession"));
        Assert.Equal(proposed, answerSession.GetProperty("sessionId").GetGuid());
        Assert.Equal(withB, answerSession.GetProperty("exchangeId").GetGuid());
        var confirm = Assert.Single(Actions(dashboard, "ConfirmSession"));
        Assert.Equal(unconfirmed, confirm.GetProperty("sessionId").GetGuid());
        Assert.Equal(withC, Assert.Single(Actions(dashboard, "FinishExchange")).GetProperty("exchangeId").GetGuid());
        Assert.Equal(withC, Assert.Single(Actions(dashboard, "AnswerExtension")).GetProperty("exchangeId").GetGuid());

        // Самое срочное сверху: срок «Урок состоялся» вышел бы раньше всех остальных сроков.
        var order = dashboard.GetProperty("actionsNeeded").EnumerateArray().Select(x => x.GetProperty("type").GetString()).ToList();
        Assert.Equal("ConfirmSession", order[0]);
        Assert.Equal(5, order.Count);
    }

    [Fact]
    public async Task Things_that_are_already_done_or_expired_do_not_appear_as_actions()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var exchangeId = await OpenExchangeAsync(world, a, b);
        var mineProposed = (await JsonAsync(await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { startAt = Now.AddDays(2).ToString("O"), teacherId = a.Id }))).GetProperty("id").GetGuid();
        var confirmedByMe = await ScheduledAsync(a, b, exchangeId, Now.AddDays(3));
        await SetSessionAsync(confirmedByMe, SessionStatus.Unconfirmed, start: Now.AddHours(-3), unconfirmedAt: Now.AddHours(-2));
        await SendAsync(a, HttpMethod.Post, $"/sessions/{confirmedByMe}/confirm-manual");        // я уже нажал
        var tooLate = await ScheduledAsync(a, b, exchangeId, Now.AddDays(4));
        await SetSessionAsync(tooLate, SessionStatus.Unconfirmed, start: Now.AddHours(-30), unconfirmedAt: Now.AddHours(-26));   // срок подтверждения вышел
        await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/complete");                // я сам уже нажал «Завершить»
        await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/extend");                  // и согласился продлить

        var dashboard = await DashboardAsync(a);

        Assert.Equal(0, dashboard.GetProperty("actionsNeeded").GetArrayLength());   // своё предложение ждёт партнёра, а не меня
        Assert.NotEqual(mineProposed, Guid.Empty);
    }

    [Fact]
    public async Task The_list_of_each_kind_of_action_is_capped()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        await _factory.QueryAsync(async db =>
        {
            // 25 входящих заявок напрямую в БД: через API лимит 5 ожидающих на отправителя.
            for (var i = 0; i < 25; i++)
            {
                var sender = User.CreateRegular(AuthApi.NewEmail(), "Sender", new DateOnly(1990, 1, 1), "en");
                db.Users.Add(sender);
                db.ExchangeRequests.Add(ExchangeRequest.Create(sender.Id, a.Id, world.S(1), world.S(0), 30, null, Now));
            }

            await db.SaveChangesAsync();
            return 0;
        });

        var dashboard = await DashboardAsync(a);

        Assert.Equal(25, dashboard.GetProperty("counters").GetProperty("incomingRequests").GetInt32());   // счётчик точный
        Assert.Equal(20, Actions(dashboard, "AnswerRequest").Count);                                     // список ограничен
    }
}
