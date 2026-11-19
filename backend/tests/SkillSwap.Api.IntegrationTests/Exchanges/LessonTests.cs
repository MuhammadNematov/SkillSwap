using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>Уроки (разделы 6.2, 6.3 и 9.3 ТЗ): слоты, ответы, перенос, отмена, пересечения, напоминания, подтверждение, календарь, баланс.</summary>
[Collection(ApiCollection.Name)]
public class LessonTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public LessonTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    /// <summary>«Сейчас» приложения: часы замороженные, все правила времени в тестах отсчитываются от них.</summary>
    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O");

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

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B, Guid ExchangeId)> ExchangeAsync()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id || u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        return (world, a, b, await OpenExchangeAsync(world, a, b));
    }

    private async Task<Guid> OpenExchangeAsync(MatchTestWorld world, MatchPerson a, MatchPerson b)
    {
        var created = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var accept = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");
        return (await JsonAsync(accept)).GetProperty("exchangeId").GetGuid();
    }

    private Task<HttpResponseMessage> ProposeAsync(MatchPerson who, Guid exchangeId, DateTime startAt, Guid teacherId, string? topic = null) =>
        SendAsync(who, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { startAt = Iso(startAt), teacherId, topic });

    private async Task<Guid> ProposedOkAsync(MatchPerson who, Guid exchangeId, DateTime startAt, Guid? teacher = null)
    {
        var response = await ProposeAsync(who, exchangeId, startAt, teacher ?? who.Id);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ScheduledAsync(MatchPerson proposer, MatchPerson answerer, Guid exchangeId, DateTime startAt)
    {
        var id = await ProposedOkAsync(proposer, exchangeId, startAt);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(answerer, HttpMethod.Post, $"/sessions/{id}/confirm")).StatusCode);
        return id;
    }

    private Task<SessionStatus> StatusAsync(Guid id) => _factory.QueryAsync(db => db.Sessions.Where(s => s.Id == id).Select(s => s.Status).SingleAsync());

    private Task<LessonSession> LoadAsync(Guid id) => _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == id));

    private Task MoveAsync(Guid id, DateTime startAt) => _factory.QueryAsync(async db =>
    {
        await db.Sessions.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.StartAt, startAt).SetProperty(x => x.EndAt, startAt.AddMinutes(45)));
        return 0;
    });

    private async Task RunJobAsync(Func<LessonSessionJobs, Task> job)
    {
        using var scope = _factory.Services.CreateScope();
        await job(scope.ServiceProvider.GetRequiredService<LessonSessionJobs>());
    }

    private async Task<List<JsonElement>> NotificationsOf(MatchPerson who, string type)
    {
        var list = (await JsonAsync(await SendAsync(who, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return list.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    // ---------- предложить время ----------

    [Fact]
    public async Task A_start_with_more_than_millisecond_precision_is_stored_exactly_so_the_jobs_scheduled_for_it_find_the_lesson()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var start = Now.AddHours(5);
        var requested = new DateTime(start.Ticks - start.Ticks % TimeSpan.TicksPerMillisecond + 1234567, DateTimeKind.Utc);   // 7 знаков после запятой

        var response = await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { startAt = requested.ToString("O"), teacherId = a.Id });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await JsonAsync(response)).GetProperty("id").GetGuid();
        await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/confirm");

        var stored = await LoadAsync(id);
        Assert.Equal(0, stored.StartAt.Ticks % TimeSpan.TicksPerMillisecond);   // без «хвоста»: то, что в памяти, равно тому, что в базе
        Assert.Equal(new DateTime(requested.Ticks - requested.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc), stored.StartAt);
        Assert.Equal(stored.StartAt.AddMinutes(45), stored.EndAt);
    }


    [Fact]
    public async Task A_proposal_creates_a_pending_session_and_a_card_in_the_chat()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var start = Now.AddHours(5);

        var response = await ProposeAsync(a, exchangeId, start, a.Id, "  Основы Python  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = await JsonAsync(response);
        Assert.Equal("Proposed", session.GetProperty("status").GetString());
        Assert.Equal(45, session.GetProperty("durationMinutes").GetInt32());
        Assert.Equal(start, session.GetProperty("startAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(start.AddMinutes(45), session.GetProperty("endAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal("Основы Python", session.GetProperty("topic").GetString());
        Assert.Equal(a.Id, session.GetProperty("teacherId").GetGuid());
        Assert.Equal(a.Id, session.GetProperty("proposedById").GetGuid());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("cancelledById").ValueKind);
        Assert.Contains($"/sessions/{session.GetProperty("id").GetGuid()}", response.Headers.Location!.ToString());

        // Карточка в истории чата: сообщение типа SessionCard с живым состоянием урока; партнёр видит её непрочитанной.
        var card = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}/messages"))).GetProperty("items")[0];
        Assert.Equal("SessionCard", card.GetProperty("type").GetString());
        Assert.Equal(session.GetProperty("id").GetGuid(), card.GetProperty("session").GetProperty("id").GetGuid());
        Assert.Equal("Proposed", card.GetProperty("session").GetProperty("status").GetString());
        Assert.Equal(1, (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("unreadMessages").GetInt32());
        Assert.Single(await NotificationsOf(b, "NewMessages"));
    }

    [Fact]
    public async Task The_partner_receives_the_card_through_the_chat_hub()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var events = new ConcurrentQueue<JsonElement>();
        await using var bConn = ChatConnection(b);
        bConn.On<JsonElement>("message", m => events.Enqueue(m));
        await bConn.StartAsync();

        var id = await ProposedOkAsync(a, exchangeId, Now.AddHours(5));

        await WaitForAsync(() => !events.IsEmpty);
        var pushed = Assert.Single(events);
        Assert.Equal("SessionCard", pushed.GetProperty("type").GetString());
        Assert.Equal(id, pushed.GetProperty("session").GetProperty("id").GetGuid());
    }

    private HubConnection ChatConnection(MatchPerson who) => new HubConnectionBuilder()
        .WithUrl(new Uri(_client.BaseAddress!, "/hubs/chat"), o =>
        {
            o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            o.AccessTokenProvider = () => Task.FromResult<string?>(who.Token);
        })
        .Build();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(40);
        }

        Assert.True(condition(), "ожидаемое событие не пришло");
    }

    [Fact]
    public async Task Either_participant_can_be_named_as_the_teacher_and_a_stranger_cannot()
    {
        var (world, a, b, exchangeId) = await ExchangeAsync();
        var stranger = await world.UserAsync(Beginner0, Beginner1);

        Assert.Equal(HttpStatusCode.Created, (await ProposeAsync(a, exchangeId, Now.AddHours(5), b.Id)).StatusCode);   // предлагаю, чтобы учил партнёр
        Assert.Equal(HttpStatusCode.Created, (await ProposeAsync(a, exchangeId, Now.AddHours(7), a.Id)).StatusCode);

        var wrong = await ProposeAsync(a, exchangeId, Now.AddHours(9), stranger.Id);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("teacher_not_in_exchange", await CodeAsync(wrong));
    }

    [Fact]
    public async Task A_slot_needs_an_hours_notice_and_must_not_start_after_the_exchange_ends()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var exchangeEnd = (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("endDate").GetDateTime();

        var soon = await ProposeAsync(a, exchangeId, Now.AddMinutes(59), a.Id);
        Assert.Equal(HttpStatusCode.BadRequest, soon.StatusCode);
        Assert.Equal("slot_too_soon", await CodeAsync(soon));
        Assert.Equal("slot_too_soon", await CodeAsync(await ProposeAsync(a, exchangeId, Now.AddHours(-3), a.Id)));
        Assert.Equal(HttpStatusCode.Created, (await ProposeAsync(a, exchangeId, Now.AddHours(1).AddSeconds(5), a.Id)).StatusCode);   // час с небольшим запасом

        var late = await ProposeAsync(a, exchangeId, exchangeEnd.AddMinutes(1), a.Id);
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Equal("slot_after_exchange_end", await CodeAsync(late));
        Assert.Equal(HttpStatusCode.Created, (await ProposeAsync(a, exchangeId, exchangeEnd.AddHours(-1), a.Id)).StatusCode);
    }

    [Fact]
    public async Task The_start_time_may_carry_any_offset_and_is_stored_in_utc()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var utc = Now.AddHours(6);
        var local = new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(5)).ToString("O");   // тот же момент, записанный с поясом +05:00

        var response = await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { startAt = local, teacherId = a.Id });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(utc, (await JsonAsync(response)).GetProperty("startAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
    }

    [Theory]
    [InlineData(false, true, null)]
    [InlineData(true, false, null)]
    [InlineData(true, true, 201)]
    public async Task The_start_and_the_teacher_are_required_and_the_topic_is_limited(bool withStart, bool withTeacher, int? topicLength)
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var body = new Dictionary<string, object?>();
        if (withStart)
        {
            body["startAt"] = Iso(Now.AddHours(5));
        }

        if (withTeacher)
        {
            body["teacherId"] = a.Id;
        }

        if (topicLength is { } n)
        {
            body["topic"] = new string('я', n);
        }

        var response = await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeAsync(response));
    }

    [Fact]
    public async Task No_more_than_five_proposals_wait_for_an_answer()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        for (var i = 0; i < 5; i++)
        {
            await ProposedOkAsync(a, exchangeId, Now.AddHours(3 + i));
        }

        var sixth = await ProposeAsync(a, exchangeId, Now.AddHours(20), a.Id);

        Assert.Equal(HttpStatusCode.Conflict, sixth.StatusCode);
        Assert.Equal("too_many_proposals", await CodeAsync(sixth));
    }

    [Fact]
    public async Task Strangers_and_closed_chats_cannot_get_slots_and_anonymous_calls_are_refused()
    {
        var (world, a, _, exchangeId) = await ExchangeAsync();
        var stranger = await world.UserAsync(Beginner0, Beginner1);

        var foreign = await ProposeAsync(stranger, exchangeId, Now.AddHours(5), stranger.Id);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("exchange_not_found", await CodeAsync(foreign));
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { })).StatusCode);

        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExchangeStatus.Frozen));
            return 0;
        });
        var frozen = await ProposeAsync(a, exchangeId, Now.AddHours(5), a.Id);
        Assert.Equal(HttpStatusCode.Conflict, frozen.StatusCode);
        Assert.Equal("chat_read_only", await CodeAsync(frozen));
    }

    [Fact]
    public async Task A_proposal_schedules_its_expiry_for_the_start_time()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();
        var before = jobs.Scheduled.Count;
        var start = Now.AddHours(5);

        await ProposedOkAsync(a, exchangeId, start);

        var job = Assert.Single(jobs.Scheduled.Skip(before), j => j.Description.Contains(nameof(LessonSessionJobs.ExpireProposalAsync)));
        Assert.True(Math.Abs((job.RunAt.UtcDateTime - start).TotalMilliseconds) < 5);
    }

    // ---------- ответ ----------

    [Fact]
    public async Task The_partner_confirms_and_the_session_is_scheduled_with_reminders_and_an_end_check()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();
        var start = Now.AddDays(3);
        var id = await ProposedOkAsync(a, exchangeId, start);
        var before = jobs.Scheduled.Count;

        var response = await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/confirm");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Scheduled", (await JsonAsync(response)).GetProperty("status").GetString());
        var scheduled = jobs.Scheduled.Skip(before).ToList();
        Assert.Equal(2, scheduled.Count(j => j.Description.Contains(nameof(LessonSessionJobs.RemindAsync))));   // за сутки и за 15 минут
        Assert.Contains(scheduled, j => j.Description.Contains(nameof(VideoLessonJobs.FirstCheckAsync)) && Math.Abs((j.RunAt.UtcDateTime - start.AddMinutes(15)).TotalMilliseconds) < 5);
        Assert.Contains(scheduled, j => j.Description.Contains(nameof(VideoLessonJobs.CloseRoomAsync)) && Math.Abs((j.RunAt.UtcDateTime - start.AddMinutes(75)).TotalMilliseconds) < 5);
        Assert.Single(await NotificationsOf(a, "SessionConfirmed"));   // автор узнал, что время принято

        var card = (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchanges/{exchangeId}/messages"))).GetProperty("items")[0];
        Assert.Equal("Scheduled", card.GetProperty("session").GetProperty("status").GetString());   // карточка показывает живое состояние
    }

    [Fact]
    public async Task A_slot_less_than_a_day_away_gets_only_the_fifteen_minute_reminder()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();
        var id = await ProposedOkAsync(a, exchangeId, Now.AddHours(10));
        var before = jobs.Scheduled.Count;

        await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/confirm");

        Assert.Equal(1, jobs.Scheduled.Skip(before).Count(j => j.Description.Contains(nameof(LessonSessionJobs.RemindAsync))));
    }

    [Fact]
    public async Task Both_sides_see_the_change_in_the_hub_event()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var forA = new ConcurrentQueue<JsonElement>();
        var forB = new ConcurrentQueue<JsonElement>();
        await using var aConn = ChatConnection(a);
        await using var bConn = ChatConnection(b);
        aConn.On<JsonElement>("sessionUpdated", s => forA.Enqueue(s));
        bConn.On<JsonElement>("sessionUpdated", s => forB.Enqueue(s));
        await aConn.StartAsync();
        await bConn.StartAsync();
        var id = await ProposedOkAsync(a, exchangeId, Now.AddHours(5));

        await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/confirm");

        await WaitForAsync(() => !forA.IsEmpty && !forB.IsEmpty);
        Assert.Equal("Scheduled", forA.Last().GetProperty("status").GetString());
        Assert.Equal(id, forB.Last().GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Only_the_partner_answers_a_proposal_and_a_closed_one_cannot_be_answered_again()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var id = await ProposedOkAsync(a, exchangeId, Now.AddHours(5));

        foreach (var action in new[] { "confirm", "decline" })
        {
            var own = await SendAsync(a, HttpMethod.Post, $"/sessions/{id}/{action}");
            Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
            Assert.Equal("not_session_answerer", await CodeAsync(own));
        }

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/decline")).StatusCode);
        Assert.Equal(SessionStatus.Declined, await StatusAsync(id));
        Assert.Single(await NotificationsOf(a, "SessionDeclined"));

        foreach (var action in new[] { "confirm", "decline" })
        {
            var again = await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/{action}");
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("invalid_state_transition", await CodeAsync(again));
        }
    }

    [Fact]
    public async Task Confirming_after_the_start_time_marks_the_proposal_expired()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var id = await ProposedOkAsync(a, exchangeId, Now.AddHours(5));
        await MoveAsync(id, Now.AddMinutes(-1));

        var response = await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/confirm");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("session_expired", await CodeAsync(response));
        Assert.Equal(SessionStatus.Expired, await StatusAsync(id));   // состояние сохранено, а не откатано
    }

    [Fact]
    public async Task Strangers_see_no_session_at_all()
    {
        var (world, a, _, exchangeId) = await ExchangeAsync();
        var stranger = await world.UserAsync(Beginner0, Beginner1);
        var id = await ProposedOkAsync(a, exchangeId, Now.AddHours(5));

        foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, $"/sessions/{id}", null), (HttpMethod.Post, $"/sessions/{id}/confirm", null),
            (HttpMethod.Post, $"/sessions/{id}/decline", null), (HttpMethod.Post, $"/sessions/{id}/cancel", null),
            (HttpMethod.Post, $"/sessions/{id}/confirm-manual", null),
            (HttpMethod.Post, $"/sessions/{id}/reschedule", new { startAt = Iso(Now.AddHours(9)) }),
            (HttpMethod.Get, $"/sessions/{Guid.NewGuid()}", null),
        })
        {
            var response = await SendAsync(stranger, method, url, body);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("session_not_found", await CodeAsync(response));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(stranger, HttpMethod.Get, $"/exchanges/{exchangeId}/sessions")).StatusCode);
        Assert.Equal(SessionStatus.Proposed, await StatusAsync(id));
    }

    // ---------- пересечения ----------

    [Fact]
    public async Task Confirming_is_refused_when_either_person_has_another_lesson_at_that_time_in_any_exchange()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var c = await world.UserAsync(Beginner1, Beginner0);
        var ab = await OpenExchangeAsync(world, a, b);
        var ac = await OpenExchangeAsync(world, a, c);
        var start = Now.AddDays(2);

        // A предложил C время, пока был свободен; потом A занял это время уроком с B. Теперь занят предлагавший, а принимает C.
        var early = await ProposedOkAsync(a, ac, start.AddMinutes(30), a.Id);
        await ScheduledAsync(a, b, ab, start);
        var byPartner = await SendAsync(c, HttpMethod.Post, $"/sessions/{early}/confirm");
        Assert.Equal(HttpStatusCode.Conflict, byPartner.StatusCode);
        var problem = await JsonAsync(byPartner);
        Assert.Equal("slot_overlap", problem.GetProperty("code").GetString());
        Assert.Equal("partner", problem.GetProperty("conflictFor").GetString());   // занят собеседник (A), а не принимающий
        Assert.Equal(SessionStatus.Proposed, await StatusAsync(early));

        // Занятым оказывается тот, кто принимает: C предлагает слот, на который занят A, и A отвечает.
        var fromC = await ProposedOkAsync(c, ac, start.AddMinutes(10), c.Id);
        var byBusy = await SendAsync(a, HttpMethod.Post, $"/sessions/{fromC}/confirm");
        Assert.Equal(HttpStatusCode.Conflict, byBusy.StatusCode);
        Assert.Equal("you", (await JsonAsync(byBusy)).GetProperty("conflictFor").GetString());
    }

    [Fact]
    public async Task The_proposer_cannot_offer_a_time_when_they_already_have_a_lesson()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var c = await world.UserAsync(Beginner1, Beginner0);
        var ab = await OpenExchangeAsync(world, a, b);
        var ac = await OpenExchangeAsync(world, a, c);
        var start = Now.AddDays(2);
        await ScheduledAsync(a, b, ab, start);

        var response = await ProposeAsync(a, ac, start.AddMinutes(44), a.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal("slot_overlap", problem.GetProperty("code").GetString());
        Assert.Equal("you", problem.GetProperty("conflictFor").GetString());
    }

    [Fact]
    public async Task Lessons_that_touch_end_to_start_do_not_overlap()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var c = await world.UserAsync(Beginner1, Beginner0);
        var ab = await OpenExchangeAsync(world, a, b);
        var ac = await OpenExchangeAsync(world, a, c);
        var start = Now.AddDays(2);
        await ScheduledAsync(a, b, ab, start);

        var after = await ProposedOkAsync(a, ac, start.AddMinutes(45), a.Id);   // следующий урок начинается ровно в момент окончания
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(c, HttpMethod.Post, $"/sessions/{after}/confirm")).StatusCode);
        var before = await ProposedOkAsync(a, ac, start.AddMinutes(-45), a.Id);   // и предыдущий заканчивается ровно в начале
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(c, HttpMethod.Post, $"/sessions/{before}/confirm")).StatusCode);
    }

    [Fact]
    public async Task Unanswered_declined_and_cancelled_lessons_do_not_block_the_time()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var c = await world.UserAsync(Beginner1, Beginner0);
        var ab = await OpenExchangeAsync(world, a, b);
        var ac = await OpenExchangeAsync(world, a, c);
        var start = Now.AddDays(2);
        await ProposedOkAsync(a, ab, start);                                    // не принято
        var declined = await ProposedOkAsync(b, ab, start.AddMinutes(5));
        await SendAsync(a, HttpMethod.Post, $"/sessions/{declined}/decline");   // отклонено
        var cancelled = await ScheduledAsync(a, b, ab, start.AddMinutes(10));
        await SendAsync(a, HttpMethod.Post, $"/sessions/{cancelled}/cancel");   // отменено

        var free = await ProposedOkAsync(a, ac, start, a.Id);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(c, HttpMethod.Post, $"/sessions/{free}/confirm")).StatusCode);
    }

    [Fact]
    public async Task Two_overlapping_slots_confirmed_at_the_same_moment_book_the_person_only_once()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var world = await MatchTestWorld.CreateAsync(_factory);
            var a = await world.UserAsync(Beginner0, Beginner1);
            var b = await world.UserAsync(Beginner1, Beginner0);
            var c = await world.UserAsync(Beginner1, Beginner0);
            var ab = await OpenExchangeAsync(world, a, b);
            var ac = await OpenExchangeAsync(world, a, c);
            var start = Now.AddDays(2);
            var one = await ProposedOkAsync(a, ab, start);
            var two = await ProposedOkAsync(a, ac, start.AddMinutes(20), a.Id);

            var results = await Task.WhenAll(
                SendAsync(b, HttpMethod.Post, $"/sessions/{one}/confirm"), SendAsync(c, HttpMethod.Post, $"/sessions/{two}/confirm"));

            Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
            Assert.Equal(1, await _factory.QueryAsync(db => db.Sessions.CountAsync(s => (s.Id == one || s.Id == two) && s.Status == SessionStatus.Scheduled)));
        }
    }

    // ---------- перенос ----------

    [Fact]
    public async Task Rescheduling_asks_the_partner_again_frees_the_old_time_and_notifies()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var c = await world.UserAsync(Beginner1, Beginner0);
        var ab = await OpenExchangeAsync(world, a, b);
        var ac = await OpenExchangeAsync(world, a, c);
        var oldStart = Now.AddDays(2);
        var newStart = Now.AddDays(3);
        var id = await ScheduledAsync(a, b, ab, oldStart);

        var response = await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/reschedule", new { startAt = Iso(newStart) });   // переносит партнёр

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var moved = await JsonAsync(response);
        Assert.Equal("Proposed", moved.GetProperty("status").GetString());
        Assert.Equal(newStart, moved.GetProperty("startAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(oldStart, moved.GetProperty("previousStartAt").GetDateTime(), TimeSpan.FromMilliseconds(1));
        Assert.Equal(b.Id, moved.GetProperty("proposedById").GetGuid());   // отвечать теперь A
        Assert.Single(await NotificationsOf(a, "SessionProposed"));

        // Старое время свободно: A может назначить там другой урок.
        var other = await ProposedOkAsync(a, ac, oldStart, a.Id);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(c, HttpMethod.Post, $"/sessions/{other}/confirm")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/confirm")).StatusCode);   // сам себе не подтверждает
        var confirmed = await SendAsync(a, HttpMethod.Post, $"/sessions/{id}/confirm");
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal("Scheduled", (await JsonAsync(confirmed)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_rescheduled_session_that_nobody_confirms_expires_at_the_new_start()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();
        var id = await ScheduledAsync(a, b, exchangeId, Now.AddDays(2));
        var before = jobs.Scheduled.Count;
        await SendAsync(a, HttpMethod.Post, $"/sessions/{id}/reschedule", new { startAt = Iso(Now.AddDays(3)) });

        Assert.Single(jobs.Scheduled.Skip(before), j => j.Description.Contains(nameof(LessonSessionJobs.ExpireProposalAsync)));
        var current = await LoadAsync(id);
        await MoveAsync(id, Now.AddMinutes(-5));
        await RunJobAsync(j => j.ExpireProposalAsync(id, current.StartAt.Ticks));   // задача на прежнее «новое» время: время уже другое, ничего не делает
        Assert.Equal(SessionStatus.Proposed, await StatusAsync(id));

        var moved = await LoadAsync(id);
        await RunJobAsync(j => j.ExpireProposalAsync(id, moved.StartAt.Ticks));
        Assert.Equal(SessionStatus.Expired, await StatusAsync(id));
    }

    [Fact]
    public async Task Rescheduling_is_impossible_within_two_hours_and_for_unconfirmed_proposals()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var soon = await ScheduledAsync(a, b, exchangeId, Now.AddHours(5));
        await MoveAsync(soon, Now.AddHours(2).AddSeconds(-1));
        var pending = await ProposedOkAsync(a, exchangeId, Now.AddDays(5));

        var tooLate = await SendAsync(a, HttpMethod.Post, $"/sessions/{soon}/reschedule", new { startAt = Iso(Now.AddDays(2)) });
        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
        Assert.Equal("reschedule_too_late", await CodeAsync(tooLate));
        Assert.Equal(SessionStatus.Scheduled, await StatusAsync(soon));

        var onProposal = await SendAsync(a, HttpMethod.Post, $"/sessions/{pending}/reschedule", new { startAt = Iso(Now.AddDays(6)) });
        Assert.Equal(HttpStatusCode.Conflict, onProposal.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(onProposal));
    }

    [Fact]
    public async Task Rescheduling_to_exactly_two_hours_before_is_still_allowed_and_the_new_time_is_validated()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var edge = await ScheduledAsync(a, b, exchangeId, Now.AddHours(5));
        await MoveAsync(edge, Now.AddHours(2).AddSeconds(2));   // чуть больше двух часов: сервер считает по своему времени
        var ok = await SendAsync(a, HttpMethod.Post, $"/sessions/{edge}/reschedule", new { startAt = Iso(Now.AddDays(1)) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var other = await ScheduledAsync(a, b, exchangeId, Now.AddDays(9));
        var tooSoon = await SendAsync(a, HttpMethod.Post, $"/sessions/{other}/reschedule", new { startAt = Iso(Now.AddMinutes(30)) });
        Assert.Equal("slot_too_soon", await CodeAsync(tooSoon));
        var afterEnd = await SendAsync(a, HttpMethod.Post, $"/sessions/{other}/reschedule", new { startAt = Iso(Now.AddDays(40)) });
        Assert.Equal("slot_after_exchange_end", await CodeAsync(afterEnd));
        Assert.Equal("validation_failed", await CodeAsync(await SendAsync(a, HttpMethod.Post, $"/sessions/{other}/reschedule", new { })));
        Assert.Equal(SessionStatus.Scheduled, await StatusAsync(other));
    }

    // ---------- отмена ----------

    [Fact]
    public async Task The_author_withdraws_a_proposal_but_the_partner_may_only_decline_it()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var id = await ProposedOkAsync(a, exchangeId, Now.AddHours(5));

        var partner = await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/cancel");
        Assert.Equal(HttpStatusCode.Forbidden, partner.StatusCode);
        Assert.Equal("not_session_proposer", await CodeAsync(partner));

        var mine = await JsonAsync(await SendAsync(a, HttpMethod.Post, $"/sessions/{id}/cancel"));
        Assert.Equal("Cancelled", mine.GetProperty("status").GetString());
        Assert.Equal(a.Id, mine.GetProperty("cancelledById").GetGuid());
    }

    [Fact]
    public async Task Cancelling_two_hours_before_is_free_and_later_is_a_late_cancellation_and_the_partner_is_told()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var free = await ScheduledAsync(a, b, exchangeId, Now.AddDays(1));
        var late = await ScheduledAsync(a, b, exchangeId, Now.AddDays(2));
        var edge = await ScheduledAsync(a, b, exchangeId, Now.AddDays(3));
        await MoveAsync(late, Now.AddHours(2).AddSeconds(-30));
        await MoveAsync(edge, Now.AddHours(2).AddSeconds(30));

        var freeResult = await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/sessions/{free}/cancel"));
        var lateResult = await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/sessions/{late}/cancel"));
        var edgeResult = await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/sessions/{edge}/cancel"));

        Assert.Equal("Cancelled", freeResult.GetProperty("status").GetString());
        Assert.Equal("LateCancelled", lateResult.GetProperty("status").GetString());
        Assert.Equal("Cancelled", edgeResult.GetProperty("status").GetString());   // чуть больше двух часов — ещё без последствий
        Assert.Equal(b.Id, lateResult.GetProperty("cancelledById").GetGuid());

        var told = await NotificationsOf(a, "SessionCancelled");
        Assert.Equal(3, told.Count);
        Assert.Single(told, n => n.GetProperty("payload").GetProperty("late").GetBoolean());
        Assert.Empty(await NotificationsOf(b, "SessionCancelled"));   // отменившему уведомление не нужно
    }

    [Fact]
    public async Task A_lesson_that_has_started_cannot_be_cancelled_and_a_closed_one_cannot_be_cancelled_twice()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var started = await ScheduledAsync(a, b, exchangeId, Now.AddDays(1));
        await MoveAsync(started, Now.AddMinutes(-10));
        var done = await ScheduledAsync(a, b, exchangeId, Now.AddDays(2));
        await SendAsync(a, HttpMethod.Post, $"/sessions/{done}/cancel");

        var late = await SendAsync(b, HttpMethod.Post, $"/sessions/{started}/cancel");
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("session_already_started", await CodeAsync(late));
        var twice = await SendAsync(b, HttpMethod.Post, $"/sessions/{done}/cancel");
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(twice));
    }

    // ---------- задачи: истечение, напоминания ----------

    [Fact]
    public async Task The_expiry_job_closes_unanswered_proposals_at_the_start_and_is_idempotent()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var id = await ProposedOkAsync(a, exchangeId, Now.AddHours(5));
        var ticks = (await LoadAsync(id)).StartAt.Ticks;

        await RunJobAsync(j => j.ExpireProposalAsync(id, ticks));   // рано
        Assert.Equal(SessionStatus.Proposed, await StatusAsync(id));

        await MoveAsync(id, Now.AddMinutes(-1));
        var moved = (await LoadAsync(id)).StartAt.Ticks;
        await RunJobAsync(j => j.ExpireProposalAsync(id, moved));
        await RunJobAsync(j => j.ExpireProposalAsync(id, moved));

        Assert.Equal(SessionStatus.Expired, await StatusAsync(id));
        Assert.Single(await NotificationsOf(a, "SessionExpired"));   // автору один раз
        await RunJobAsync(j => j.ExpireProposalAsync(Guid.NewGuid(), 1));   // неизвестный урок — без ошибки
    }

    [Fact]
    public async Task Reminders_reach_both_in_the_interface_once_and_only_for_a_scheduled_lesson()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var id = await ScheduledAsync(a, b, exchangeId, Now.AddDays(2));
        var ticks = (await LoadAsync(id)).StartAt.Ticks;

        await RunJobAsync(j => j.RemindAsync(id, ticks, 1440));
        await RunJobAsync(j => j.RemindAsync(id, ticks, 1440));   // повтор после сбоя не шлёт второе
        await RunJobAsync(j => j.RemindAsync(id, ticks, 15));

        foreach (var (person, partner) in new[] { (a, b), (b, a) })
        {
            var reminders = await NotificationsOf(person, "SessionReminder");
            Assert.Equal(2, reminders.Count);
            Assert.Equal(new[] { 15, 1440 }, reminders.Select(r => r.GetProperty("payload").GetProperty("minutesBefore").GetInt32()).Order());
            Assert.Equal(partner.User.Name, reminders[0].GetProperty("payload").GetProperty("userName").GetString());
            Assert.Equal(id, reminders[0].GetProperty("payload").GetProperty("sessionId").GetGuid());
        }

        var cancelled = await ScheduledAsync(a, b, exchangeId, Now.AddDays(4));
        var cancelledTicks = (await LoadAsync(cancelled)).StartAt.Ticks;
        await SendAsync(a, HttpMethod.Post, $"/sessions/{cancelled}/cancel");
        await RunJobAsync(j => j.RemindAsync(cancelled, cancelledTicks, 1440));
        Assert.Equal(2, (await NotificationsOf(a, "SessionReminder")).Count);   // об отменённом уроке не напоминаем
    }

    [Fact]
    public async Task A_reminder_for_an_old_time_after_a_reschedule_does_nothing_and_a_new_one_works()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var id = await ScheduledAsync(a, b, exchangeId, Now.AddDays(2));
        var oldTicks = (await LoadAsync(id)).StartAt.Ticks;
        await SendAsync(a, HttpMethod.Post, $"/sessions/{id}/reschedule", new { startAt = Iso(Now.AddDays(3)) });
        await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/confirm");

        await RunJobAsync(j => j.RemindAsync(id, oldTicks, 1440));
        Assert.Empty(await NotificationsOf(a, "SessionReminder"));

        var newTicks = (await LoadAsync(id)).StartAt.Ticks;
        await RunJobAsync(j => j.RemindAsync(id, newTicks, 1440));
        Assert.Single(await NotificationsOf(a, "SessionReminder"));
    }

    [Theory]
    [InlineData("en", "Europe/London", "lesson in 24 hours")]
    [InlineData("ru", "Asia/Dushanbe", "урок через 24 часа")]
    [InlineData("tg", "Asia/Dushanbe", "дарс пас аз 24 соат")]
    public async Task The_reminder_email_is_in_the_users_language_and_local_time(string language, string timeZone, string subjectPart)
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.UiLanguage, language).SetProperty(u => u.TimeZone, timeZone));
            return 0;
        });
        var start = new DateTime(Now.Year, Now.Month, Now.Day, 12, 0, 0, DateTimeKind.Utc).AddDays(2);   // 12:00 UTC
        var id = await ScheduledAsync(a, b, exchangeId, start);
        var ticks = (await LoadAsync(id)).StartAt.Ticks;

        await RunJobAsync(j => j.RemindAsync(id, ticks, 1440));

        var mail = Assert.Single(_factory.Mail.To(b.User.Email!), m => m.Subject.Contains(subjectPart));
        Assert.Contains(a.User.Name, mail.Body);
        Assert.Contains($"/exchanges/{exchangeId}", mail.Body);
        var local = TimeZoneInfo.ConvertTimeFromUtc(start, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        Assert.Contains($"{local:dd.MM.yyyy HH:mm} ({timeZone})", mail.Body);   // время в поясе получателя, с названием пояса
        Assert.Single(_factory.Mail.To(a.User.Email!), m => m.Subject.Contains("lesson in 24 hours"));   // у A (en) по-английски и в его поясе
    }

    [Fact]
    public async Task Only_the_reminders_are_emailed_other_lesson_events_stay_in_the_interface()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var lettersA = _factory.Mail.To(a.User.Email!).Count;
        var lettersB = _factory.Mail.To(b.User.Email!).Count;
        var id = await ScheduledAsync(a, b, exchangeId, Now.AddDays(2));
        await SendAsync(a, HttpMethod.Post, $"/sessions/{id}/reschedule", new { startAt = Iso(Now.AddDays(3)) });
        await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/decline");
        var second = await ScheduledAsync(a, b, exchangeId, Now.AddDays(5));
        await SendAsync(b, HttpMethod.Post, $"/sessions/{second}/cancel");

        Assert.Equal(lettersA, _factory.Mail.To(a.User.Email!).Count);
        Assert.Equal(lettersB, _factory.Mail.To(b.User.Email!).Count);
    }

    // ---------- после урока ----------

    private async Task<(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid SessionId)> FinishedLessonAsync()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var id = await ScheduledAsync(a, b, exchangeId, Now.AddDays(2));
        var ticks = (await LoadAsync(id)).StartAt.Ticks;
        await MoveAsync(id, Now.AddHours(-2));   // урок давно закончился и комната закрыта
        var moved = (await LoadAsync(id)).StartAt.Ticks;
        var start = new DateTime(moved, DateTimeKind.Utc);

        // Оба были в комнате, но совместно всего 10 минут: итог — Unconfirmed.
        await _factory.QueryAsync(async db =>
        {
            db.Add(SessionAttendance.Restore(id, a.Id, start.AddMinutes(5), start.AddMinutes(15)));
            db.Add(SessionAttendance.Restore(id, b.Id, start.AddMinutes(5), start.AddMinutes(15)));
            await db.Sessions.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.InProgress));
            await db.SaveChangesAsync();
            return 0;
        });

        await RunVideoJobAsync(j => j.CloseRoomAsync(id, ticks));      // задача на прежнее время ничего не делает
        Assert.Equal(SessionStatus.InProgress, await StatusAsync(id));
        await RunVideoJobAsync(j => j.CloseRoomAsync(id, moved));
        return (a, b, exchangeId, id);
    }

    private async Task RunVideoJobAsync(Func<VideoLessonJobs, Task> job)
    {
        using var scope = _factory.Services.CreateScope();
        await job(scope.ServiceProvider.GetRequiredService<VideoLessonJobs>());
    }

    [Fact]
    public async Task When_the_room_closes_with_less_than_thirty_joint_minutes_the_lesson_waits_for_confirmation_and_both_are_asked()
    {
        var (a, b, _, id) = await FinishedLessonAsync();

        Assert.Equal(SessionStatus.Unconfirmed, await StatusAsync(id));
        Assert.Single(await NotificationsOf(a, "SessionNeedsConfirmation"));
        Assert.Single(await NotificationsOf(b, "SessionNeedsConfirmation"));
        var dto = await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/sessions/{id}"));
        Assert.NotEqual(JsonValueKind.Null, dto.GetProperty("confirmDeadlineAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, dto.GetProperty("unconfirmedAt").ValueKind);
    }

    [Fact]
    public async Task Both_confirming_the_lesson_completes_it_and_it_counts_in_the_balance_of_its_teacher()
    {
        var (a, b, exchangeId, id) = await FinishedLessonAsync();   // преподаёт A (предлагал A)

        var first = await JsonAsync(await SendAsync(a, HttpMethod.Post, $"/sessions/{id}/confirm-manual"));
        Assert.Equal("Unconfirmed", first.GetProperty("status").GetString());
        Assert.Equal(new[] { a.Id }, first.GetProperty("confirmedBy").EnumerateArray().Select(x => x.GetGuid()));
        var again = await JsonAsync(await SendAsync(a, HttpMethod.Post, $"/sessions/{id}/confirm-manual"));   // повтор не ошибка
        Assert.Equal("Unconfirmed", again.GetProperty("status").GetString());
        Assert.Equal(0, (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("balance").GetProperty("myLessons").GetInt32());

        var done = await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/confirm-manual"));
        Assert.Equal("Completed", done.GetProperty("status").GetString());
        Assert.Single(await NotificationsOf(a, "SessionCompleted"));
        Assert.Single(await NotificationsOf(b, "SessionCompleted"));

        var balanceA = (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("balance");
        var balanceB = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("balance");
        Assert.Equal((1, 0), (balanceA.GetProperty("myLessons").GetInt32(), balanceA.GetProperty("partnerLessons").GetInt32()));
        Assert.Equal((0, 1), (balanceB.GetProperty("myLessons").GetInt32(), balanceB.GetProperty("partnerLessons").GetInt32()));   // у партнёра зеркально
    }

    [Fact]
    public async Task A_lesson_nobody_confirms_in_24_hours_is_not_counted()
    {
        var (a, b, exchangeId, id) = await FinishedLessonAsync();
        await SendAsync(a, HttpMethod.Post, $"/sessions/{id}/confirm-manual");   // подтвердил один

        await RunJobAsync(j => j.ExpireUnconfirmedAsync(id));   // рано
        Assert.Equal(SessionStatus.Unconfirmed, await StatusAsync(id));

        await _factory.QueryAsync(async db =>
        {
            await db.Sessions.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UnconfirmedAt, Now.AddHours(-25)));
            return 0;
        });
        var closed = await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/confirm-manual");   // срок вышел, задача ещё не отработала
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);
        Assert.Equal("confirm_window_closed", await CodeAsync(closed));

        await RunJobAsync(j => j.ExpireUnconfirmedAsync(id));
        await RunJobAsync(j => j.ExpireUnconfirmedAsync(id));   // идемпотентна

        Assert.Equal(SessionStatus.NotCounted, await StatusAsync(id));
        Assert.Single(await NotificationsOf(a, "SessionNotCounted"));
        Assert.Single(await NotificationsOf(b, "SessionNotCounted"));
        var balance = (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("balance");
        Assert.Equal(0, balance.GetProperty("myLessons").GetInt32());   // не засчитан
    }

    [Fact]
    public async Task Manual_confirmation_is_only_for_lessons_that_have_ended()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var scheduled = await ScheduledAsync(a, b, exchangeId, Now.AddDays(1));
        var proposed = await ProposedOkAsync(a, exchangeId, Now.AddDays(2));

        foreach (var id in new[] { scheduled, proposed })
        {
            var response = await SendAsync(a, HttpMethod.Post, $"/sessions/{id}/confirm-manual");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("invalid_state_transition", await CodeAsync(response));
        }
    }

    [Fact]
    public async Task The_balance_reminder_appears_when_the_difference_exceeds_three()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await ProposedOkAsync(a, exchangeId, Now.AddDays(2 + i)));   // все четыре проводит A
        }

        await _factory.QueryAsync(async db =>
        {
            await db.Sessions.Where(s => ids.Contains(s.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.Completed));
            return 0;
        });

        var balance = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("balance");
        Assert.Equal((0, 4, true), (balance.GetProperty("myLessons").GetInt32(), balance.GetProperty("partnerLessons").GetInt32(), balance.GetProperty("showReminder").GetBoolean()));

        await _factory.QueryAsync(async db =>
        {
            await db.Sessions.Where(s => s.Id == ids[3]).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SessionStatus.NotCounted));
            return 0;
        });
        var three = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("balance");
        Assert.Equal((3, false), (three.GetProperty("partnerLessons").GetInt32(), three.GetProperty("showReminder").GetBoolean()));   // 3 : 0 — ещё не повод
    }

    // ---------- обмен закрывается ----------

    [Theory]
    [InlineData("abort")]
    [InlineData("block")]
    [InlineData("complete")]
    public async Task When_the_exchange_closes_open_lessons_are_cancelled_without_a_culprit(string how)
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var proposed = await ProposedOkAsync(a, exchangeId, Now.AddDays(2));
        var scheduled = await ScheduledAsync(a, b, exchangeId, Now.AddDays(3));
        var declined = await ProposedOkAsync(a, exchangeId, Now.AddDays(4));
        await SendAsync(b, HttpMethod.Post, $"/sessions/{declined}/decline");
        var forB = new ConcurrentQueue<JsonElement>();
        await using var bConn = ChatConnection(b);
        bConn.On<JsonElement>("sessionUpdated", s => forB.Enqueue(s));
        await bConn.StartAsync();

        switch (how)
        {
            case "abort":
                await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/abort", new { reason = "переезд" });
                break;
            case "block":
                await SendAsync(a, HttpMethod.Post, $"/users/{b.Id}/block");
                break;
            default:
                await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/complete");
                await SendAsync(b, HttpMethod.Post, $"/exchanges/{exchangeId}/complete");
                break;
        }

        foreach (var id in new[] { proposed, scheduled })
        {
            var session = await LoadAsync(id);
            Assert.Equal(SessionStatus.Cancelled, session.Status);
            Assert.Null(session.CancelledById);   // виновного нет
        }

        Assert.Equal(SessionStatus.Declined, await StatusAsync(declined));   // закрытые не трогаются
        await WaitForAsync(() => forB.Count >= 2);   // карточки в чате обновились у обоих
        Assert.All(forB, e => Assert.Equal("Cancelled", e.GetProperty("status").GetString()));
        Assert.Empty(await NotificationsOf(a, "SessionCancelled"));   // отдельных уведомлений нет: об обмене сообщает его собственное
    }

    [Fact]
    public async Task Unconfirmed_and_completed_lessons_survive_the_end_of_the_exchange_and_can_still_be_confirmed()
    {
        var (a, b, exchangeId, id) = await FinishedLessonAsync();

        await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/abort", new { reason = "x" });

        Assert.Equal(SessionStatus.Unconfirmed, await StatusAsync(id));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(a, HttpMethod.Post, $"/sessions/{id}/confirm-manual")).StatusCode);
        var done = await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/sessions/{id}/confirm-manual"));
        Assert.Equal("Completed", done.GetProperty("status").GetString());
    }

    [Fact]
    public async Task In_a_closed_exchange_slots_cannot_be_answered_rescheduled_or_cancelled()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var id = await ProposedOkAsync(a, exchangeId, Now.AddDays(2));
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExchangeStatus.Frozen));
            return 0;
        });

        foreach (var (who, action) in new[] { (b, "confirm"), (b, "decline"), (a, "cancel") })
        {
            var response = await SendAsync(who, HttpMethod.Post, $"/sessions/{id}/{action}");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("exchange_not_active", await CodeAsync(response));
        }
    }

    // ---------- карточка в чате ----------

    [Fact]
    public async Task A_lesson_card_cannot_be_edited_or_deleted()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        await ProposedOkAsync(a, exchangeId, Now.AddHours(5));
        var cardId = (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchanges/{exchangeId}/messages"))).GetProperty("items")[0].GetProperty("id").GetGuid();
        await using var conn = ChatConnection(a);
        await conn.StartAsync();

        var edit = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => conn.InvokeAsync<JsonElement>("Edit", cardId, "текст"));
        Assert.Contains("message_not_editable", edit.Message);
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => conn.InvokeAsync<JsonElement>("Delete", cardId));
    }

    // ---------- списки и календарь ----------

    [Fact]
    public async Task The_exchange_list_shows_all_statuses_newest_start_first_to_participants_only()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var early = await ProposedOkAsync(a, exchangeId, Now.AddDays(2));
        var late = await ProposedOkAsync(a, exchangeId, Now.AddDays(5));
        await SendAsync(b, HttpMethod.Post, $"/sessions/{early}/decline");

        var list = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}/sessions"))).EnumerateArray().ToList();

        Assert.Equal(new[] { late, early }, list.Select(x => x.GetProperty("id").GetGuid()));
        Assert.Equal(new[] { "Proposed", "Declined" }, list.Select(x => x.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task The_calendar_gathers_my_lessons_from_all_exchanges_in_time_order_with_the_partner()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var c = await world.UserAsync(Beginner1, Beginner0);
        var ab = await OpenExchangeAsync(world, a, b);
        var ac = await OpenExchangeAsync(world, a, c);
        var later = await ScheduledAsync(a, b, ab, Now.AddDays(6));
        var sooner = await ProposedOkAsync(c, ac, Now.AddDays(3), c.Id);
        var cancelled = await ScheduledAsync(a, b, ab, Now.AddDays(4));
        await SendAsync(a, HttpMethod.Post, $"/sessions/{cancelled}/cancel");
        var farAway = await ScheduledAsync(a, b, ab, Now.AddDays(20));

        var calendar = (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/me/sessions?from={Uri.EscapeDataString(Iso(Now))}&to={Uri.EscapeDataString(Iso(Now.AddDays(10)))}"))).EnumerateArray().ToList();

        Assert.Equal(new[] { sooner, later }, calendar.Select(x => x.GetProperty("session").GetProperty("id").GetGuid()));   // без отменённого и вне промежутка
        Assert.Equal(c.Id, calendar[0].GetProperty("partner").GetProperty("id").GetGuid());
        Assert.Equal(b.Id, calendar[1].GetProperty("partner").GetProperty("id").GetGuid());
        Assert.Equal(b.User.Name, calendar[1].GetProperty("partner").GetProperty("name").GetString());

        var withClosed = (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/me/sessions?from={Uri.EscapeDataString(Iso(Now))}&to={Uri.EscapeDataString(Iso(Now.AddDays(10)))}&includeClosed=true"))).EnumerateArray().ToList();
        Assert.Equal(3, withClosed.Count);
        Assert.Contains(withClosed, x => x.GetProperty("session").GetProperty("id").GetGuid() == cancelled);

        var defaults = (await JsonAsync(await SendAsync(a, HttpMethod.Get, "/me/sessions"))).EnumerateArray().Select(x => x.GetProperty("session").GetProperty("id").GetGuid()).ToList();
        Assert.Contains(farAway, defaults);                              // по умолчанию ближайшие 30 дней
        Assert.Empty((await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/me/sessions?from={Uri.EscapeDataString(Iso(Now.AddDays(2)))}&to={Uri.EscapeDataString(Iso(Now.AddDays(3)))}"))).EnumerateArray());
        Assert.Empty((await JsonAsync(await SendAsync(await world.UserAsync(Beginner0, Beginner1), HttpMethod.Get, "/me/sessions"))).EnumerateArray());   // чужих уроков нет
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(5, 4)]
    [InlineData(0, 101)]
    public async Task The_calendar_range_must_be_positive_and_up_to_100_days(int fromDays, int toDays)
    {
        var (_, a, _, _) = await ExchangeAsync();

        var response = await SendAsync(a, HttpMethod.Get, $"/me/sessions?from={Uri.EscapeDataString(Iso(Now.AddDays(fromDays)))}&to={Uri.EscapeDataString(Iso(Now.AddDays(toDays)))}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_range", await CodeAsync(response));
    }

    [Fact]
    public async Task The_calendar_needs_a_login_and_a_regular_account()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/me/sessions")).StatusCode);
        var world = await MatchTestWorld.CreateAsync(_factory);
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Moderator);
        await world.ModifyAsync(db => { db.Users.Add(staff); return Task.CompletedTask; });

        var response = await SendAsync(new MatchPerson(staff, TestTokens.ForUser(_factory, staff)), HttpMethod.Get, "/me/sessions");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
