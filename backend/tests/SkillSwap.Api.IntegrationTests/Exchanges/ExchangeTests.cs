using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Exchanges;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>Обмены (разделы 6.1, 9.2 и 12 ТЗ): создание при принятии заявки, лимит, завершение, выход, продление, срок.</summary>
[Collection(ApiCollection.Name)]
public class ExchangeTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ExchangeTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    private async Task<HttpResponseMessage> SendAsync(MatchPerson who, HttpMethod method, string url, object? body = null)
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

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    /// <summary>Отправитель A умеет навык 0 и учится навыку 1; получатель умеет навык 1 и учится навыку 0.</summary>
    private static Task<MatchPerson> RecipientAsync(MatchTestWorld world) => world.UserAsync(Beginner1, Beginner0);

    private async Task<Guid> RequestAsync(MatchTestWorld world, MatchPerson from, MatchPerson to, Guid? fromSkill = null)
    {
        var response = await SendAsync(from, HttpMethod.Post, "/exchange-requests", new
        {
            toUserId = to.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = fromSkill ?? world.S(0), durationDays = 30,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Заявка и её принятие: возвращает id обмена.</summary>
    private async Task<Guid> OpenExchangeAsync(MatchTestWorld world, MatchPerson from, MatchPerson to)
    {
        var id = await RequestAsync(world, from, to);
        var accept = await SendAsync(to, HttpMethod.Post, $"/exchange-requests/{id}/accept");
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        return (await JsonAsync(accept)).GetProperty("exchangeId").GetGuid();
    }

    private Task<ExchangeStatus> StatusAsync(Guid exchangeId) =>
        _factory.QueryAsync(db => db.Exchanges.Where(e => e.Id == exchangeId).Select(e => e.Status).SingleAsync());

    private Task SetStatusAsync(Guid exchangeId, ExchangeStatus status) => _factory.QueryAsync(async db =>
    {
        await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, status));
        return 0;
    });

    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B, Guid ExchangeId)> ExchangeAsync()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await RecipientAsync(world);
        return (world, a, b, await OpenExchangeAsync(world, a, b));
    }

    // ---------- создание при принятии ----------

    [Fact]
    public async Task Accepting_creates_the_exchange_with_both_skills_and_each_side_sees_it_from_its_own_view()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await RecipientAsync(world);
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();
        var requestId = await RequestAsync(world, a, b);
        var before = jobs.Scheduled.Count;

        var accept = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");

        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        var exchangeId = (await JsonAsync(accept)).GetProperty("exchangeId").GetGuid();

        var seenByA = await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchanges/{exchangeId}"));
        Assert.Equal("Active", seenByA.GetProperty("status").GetString());
        Assert.Equal(b.Id, seenByA.GetProperty("partner").GetProperty("id").GetGuid());
        Assert.Equal(world.S(0), seenByA.GetProperty("iSkillTeach").GetProperty("id").GetGuid());
        Assert.Equal(world.S(1), seenByA.GetProperty("iSkillLearn").GetProperty("id").GetGuid());
        Assert.Equal(requestId, seenByA.GetProperty("requestId").GetGuid());
        Assert.False(seenByA.GetProperty("isExtended").GetBoolean());
        var start = seenByA.GetProperty("startDate").GetDateTime();
        Assert.Equal(30, Math.Round((seenByA.GetProperty("endDate").GetDateTime() - start).TotalDays));
        Assert.Equal(0, seenByA.GetProperty("balance").GetProperty("myLessons").GetInt32());
        Assert.False(seenByA.GetProperty("balance").GetProperty("showReminder").GetBoolean());

        var seenByB = await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}"));
        Assert.Equal(a.Id, seenByB.GetProperty("partner").GetProperty("id").GetGuid());
        Assert.Equal(world.S(1), seenByB.GetProperty("iSkillTeach").GetProperty("id").GetGuid());   // у получателя навыки зеркальны
        Assert.Equal(world.S(0), seenByB.GetProperty("iSkillLearn").GetProperty("id").GetGuid());

        // Автозавершение поставлено на EndDate.
        var job = Assert.Single(jobs.Scheduled.Skip(before), j => j.Description.Contains(nameof(ExchangeJobs.CompleteDueAsync)));
        Assert.True(Math.Abs((seenByA.GetProperty("endDate").GetDateTime() - job.RunAt.UtcDateTime).TotalMilliseconds) < 5);
    }

    [Fact]
    public async Task A_request_without_the_senders_skill_makes_an_exchange_with_the_chosen_skill()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync([Beginner0, (2, SkillLevel.Beginner)], [Beginner1]);
        var b = await RecipientAsync(world);
        var response = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), durationDays = 60 });
        var requestId = (await JsonAsync(response)).GetProperty("id").GetGuid();

        var accept = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept", new { fromTeachesSkillId = world.S(2) });

        var exchange = await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchanges/{(await JsonAsync(accept)).GetProperty("exchangeId").GetGuid()}"));
        Assert.Equal(world.S(2), exchange.GetProperty("iSkillTeach").GetProperty("id").GetGuid());
        Assert.Equal(60, Math.Round((exchange.GetProperty("endDate").GetDateTime() - exchange.GetProperty("startDate").GetDateTime()).TotalDays));
    }

    [Fact]
    public async Task A_failed_accept_leaves_no_exchange_behind()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync([Beginner0, (2, SkillLevel.Beginner)], [Beginner1]);
        var b = await RecipientAsync(world);
        var response = await SendAsync(a, HttpMethod.Post, "/exchange-requests", new { toUserId = b.Id, toTeachesSkillId = world.S(1), durationDays = 30 });
        var requestId = (await JsonAsync(response)).GetProperty("id").GetGuid();

        var refused = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");   // нужен выбор навыка

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False(await _factory.QueryAsync(db => db.Exchanges.AnyAsync(e => e.RequestId == requestId)));
    }

    [Fact]
    public async Task The_request_shows_its_exchange_id_only_after_accepting()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await RecipientAsync(world);
        var requestId = await RequestAsync(world, a, b);

        Assert.Equal(JsonValueKind.Null, (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchange-requests/{requestId}"))).GetProperty("exchangeId").ValueKind);

        await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");

        var view = await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchange-requests/{requestId}"));
        Assert.NotEqual(JsonValueKind.Null, view.GetProperty("exchangeId").ValueKind);
    }

    // ---------- лимит активных обменов ----------

    private async Task<(MatchTestWorld World, MatchPerson Busy, List<Guid> Exchanges)> BusyUserAsync(int count)
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var busy = await world.UserAsync(Beginner0, Beginner1);
        var exchanges = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            exchanges.Add(await OpenExchangeAsync(world, busy, await RecipientAsync(world)));
        }

        return (world, busy, exchanges);
    }

    [Fact]
    public async Task The_fourth_request_from_a_user_with_three_active_exchanges_is_refused_at_sending()
    {
        var (world, busy, _) = await BusyUserAsync(3);
        var fresh = await RecipientAsync(world);

        var response = await SendAsync(busy, HttpMethod.Post, "/exchange-requests",
            new { toUserId = fresh.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal("exchange_limit_reached", problem.GetProperty("code").GetString());
        Assert.Equal("you", problem.GetProperty("limitReachedFor").GetString());
    }

    [Fact]
    public async Task Accept_is_refused_with_409_when_the_sender_is_at_the_limit_and_the_request_stays_pending()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await RecipientAsync(world);
        var requestId = await RequestAsync(world, a, b);
        for (var i = 0; i < 3; i++)   // уже после отправки заявки A успел завести три обмена
        {
            await OpenExchangeAsync(world, a, await RecipientAsync(world));
        }

        var accept = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");

        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
        var problem = await JsonAsync(accept);
        Assert.Equal("exchange_limit_reached", problem.GetProperty("code").GetString());
        Assert.Equal("partner", problem.GetProperty("limitReachedFor").GetString());
        Assert.Equal(ExchangeRequestStatus.Pending, await _factory.QueryAsync(db => db.ExchangeRequests.Where(r => r.Id == requestId).Select(r => r.Status).SingleAsync()));
        Assert.False(await _factory.QueryAsync(db => db.Exchanges.AnyAsync(e => e.RequestId == requestId)));
    }

    [Fact]
    public async Task Accept_is_refused_when_the_recipient_is_at_the_limit_and_works_again_after_one_is_finished()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var recipient = await RecipientAsync(world);
        var partners = new List<MatchPerson>();
        var exchanges = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var other = await world.UserAsync(Beginner0, Beginner1);
            partners.Add(other);
            exchanges.Add(await OpenExchangeAsync(world, other, recipient));
        }

        var sender = await world.UserAsync(Beginner0, Beginner1);
        var requestId = await RequestAsync(world, sender, recipient);

        var accept = await SendAsync(recipient, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");

        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
        var problem = await JsonAsync(accept);
        Assert.Equal("exchange_limit_reached", problem.GetProperty("code").GetString());
        Assert.Equal("you", problem.GetProperty("limitReachedFor").GetString());

        // Одно из трёх завершили: место освободилось.
        await SetStatusAsync(exchanges[0], ExchangeStatus.Completed);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(recipient, HttpMethod.Post, $"/exchange-requests/{requestId}/accept")).StatusCode);
    }

    [Fact]
    public async Task A_frozen_exchange_still_counts_towards_the_limit_but_a_finished_one_does_not()
    {
        var (world, busy, exchanges) = await BusyUserAsync(3);
        var fresh = await RecipientAsync(world);
        object Body() => new { toUserId = fresh.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 };

        await SetStatusAsync(exchanges[0], ExchangeStatus.Frozen);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(busy, HttpMethod.Post, "/exchange-requests", Body())).StatusCode);

        await SetStatusAsync(exchanges[0], ExchangeStatus.Aborted);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(busy, HttpMethod.Post, "/exchange-requests", Body())).StatusCode);
    }

    [Fact]
    public async Task Two_people_with_an_active_exchange_cannot_start_a_second_one()
    {
        var (world, a, b, _) = await ExchangeAsync();

        var response = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("exchange_already_active", await CodeAsync(response));
    }

    [Fact]
    public async Task Parallel_accepts_by_a_recipient_with_two_exchanges_let_exactly_one_through()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var world = await MatchTestWorld.CreateAsync(_factory);
            var recipient = await RecipientAsync(world);
            for (var i = 0; i < 2; i++)
            {
                await OpenExchangeAsync(world, await world.UserAsync(Beginner0, Beginner1), recipient);
            }

            var requests = new List<Guid>();
            for (var i = 0; i < 4; i++)
            {
                requests.Add(await RequestAsync(world, await world.UserAsync(Beginner0, Beginner1), recipient));
            }

            var results = await Task.WhenAll(requests.Select(id => SendAsync(recipient, HttpMethod.Post, $"/exchange-requests/{id}/accept")));

            Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            Assert.Equal(3, await _factory.QueryAsync(db => db.Exchanges.CountAsync(e => e.UserAId == recipient.Id || e.UserBId == recipient.Id)));
        }
    }

    [Fact]
    public async Task Accepting_the_same_request_twice_in_parallel_creates_one_exchange()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await RecipientAsync(world);
        var requestId = await RequestAsync(world, a, b);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept")));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await _factory.QueryAsync(db => db.Exchanges.CountAsync(e => e.RequestId == requestId)));
    }

    // ---------- завершение ----------

    [Fact]
    public async Task The_exchange_completes_when_both_press_finish()
    {
        var (_, a, b, id) = await ExchangeAsync();

        var first = await JsonAsync(await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/complete"));
        Assert.Equal("Active", first.GetProperty("status").GetString());
        Assert.True(first.GetProperty("completedByMe").GetBoolean());
        Assert.False(first.GetProperty("completedByPartner").GetBoolean());

        var again = await JsonAsync(await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/complete"));   // повтор не ошибка
        Assert.Equal("Active", again.GetProperty("status").GetString());

        var seenByB = await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{id}"));
        Assert.True(seenByB.GetProperty("completedByPartner").GetBoolean());

        var done = await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/exchanges/{id}/complete"));
        Assert.Equal("Completed", done.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("closedAt").ValueKind);

        var late = await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/complete");
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(late));
    }

    [Fact]
    public async Task Both_pressing_finish_at_once_complete_it_exactly_once()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (_, a, b, id) = await ExchangeAsync();

            var results = await Task.WhenAll(
                SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/complete"), SendAsync(b, HttpMethod.Post, $"/exchanges/{id}/complete"));

            Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Equal(ExchangeStatus.Completed, await StatusAsync(id));
        }
    }

    // ---------- досрочный выход ----------

    [Fact]
    public async Task Aborting_records_the_reason_and_who_left()
    {
        var (_, a, b, id) = await ExchangeAsync();

        var aborted = await JsonAsync(await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/abort", new { reason = "  Нет времени  " }));

        Assert.Equal("Aborted", aborted.GetProperty("status").GetString());
        Assert.Equal("me", aborted.GetProperty("abortedBy").GetString());
        Assert.Equal("Нет времени", aborted.GetProperty("abortReason").GetString());
        var partnerView = await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{id}"));
        Assert.Equal("partner", partnerView.GetProperty("abortedBy").GetString());

        foreach (var action in new[] { "abort", "complete", "extend" })
        {
            var response = await SendAsync(b, HttpMethod.Post, $"/exchanges/{id}/{action}", new { reason = "ещё раз" });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_reason_is_required_to_abort(string? reason)
    {
        var (_, a, _, id) = await ExchangeAsync();

        var response = await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/abort", new { reason });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ExchangeStatus.Active, await StatusAsync(id));
    }

    [Fact]
    public async Task A_reason_over_500_characters_and_a_missing_body_are_refused()
    {
        var (_, a, _, id) = await ExchangeAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/abort", new { reason = new string('я', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/abort")).StatusCode);
        Assert.Equal(ExchangeStatus.Active, await StatusAsync(id));
    }

    [Fact]
    public async Task Aborting_frees_the_place_in_the_limit()
    {
        var (world, busy, exchanges) = await BusyUserAsync(3);
        await SendAsync(busy, HttpMethod.Post, $"/exchanges/{exchanges[1]}/abort", new { reason = "уезжаю" });

        var response = await SendAsync(busy, HttpMethod.Post, "/exchange-requests",
            new { toUserId = (await RecipientAsync(world)).Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---------- продление ----------

    [Fact]
    public async Task The_extension_needs_both_and_adds_30_days_once()
    {
        var (_, a, b, id) = await ExchangeAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();
        var original = (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchanges/{id}"))).GetProperty("endDate").GetDateTime();

        var first = await JsonAsync(await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/extend"));
        Assert.False(first.GetProperty("isExtended").GetBoolean());
        Assert.True(first.GetProperty("myExtensionAccepted").GetBoolean());
        Assert.Equal(original, first.GetProperty("endDate").GetDateTime());
        var partnerSees = await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{id}"));
        Assert.True(partnerSees.GetProperty("partnerExtensionAccepted").GetBoolean());

        var before = jobs.Scheduled.Count;
        var second = await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/exchanges/{id}/extend"));
        Assert.True(second.GetProperty("isExtended").GetBoolean());
        Assert.Equal(original.AddDays(30), second.GetProperty("endDate").GetDateTime());
        Assert.False(second.GetProperty("myExtensionAccepted").GetBoolean());   // согласия израсходованы
        Assert.Single(jobs.Scheduled.Skip(before), j => j.Description.Contains(nameof(ExchangeJobs.CompleteDueAsync)));   // новый срок — новая задача

        var third = await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/extend");
        Assert.Equal(HttpStatusCode.BadRequest, third.StatusCode);
        Assert.Equal("already_extended", await CodeAsync(third));
    }

    [Fact]
    public async Task A_frozen_exchange_cannot_be_extended_or_aborted_by_a_participant()
    {
        var (_, a, _, id) = await ExchangeAsync();
        await SetStatusAsync(id, ExchangeStatus.Frozen);

        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/extend")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/abort", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/complete")).StatusCode);
    }

    // ---------- завершение по сроку ----------

    private async Task RunDueJobAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExchangeJobs>().CompleteDueAsync(id);
    }

    private Task MoveEndDateAsync(Guid id, TimeSpan fromNow) => _factory.QueryAsync(async db =>
    {
        var end = DateTime.UtcNow + fromNow;
        await db.Exchanges.Where(e => e.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.EndDate, end).SetProperty(e => e.StartDate, end.AddDays(-30)));
        return 0;
    });

    [Fact]
    public async Task The_job_completes_an_exchange_only_after_its_end_date_and_is_idempotent()
    {
        var (_, _, _, id) = await ExchangeAsync();

        await RunDueJobAsync(id);                                    // срок не вышел
        Assert.Equal(ExchangeStatus.Active, await StatusAsync(id));

        await MoveEndDateAsync(id, TimeSpan.FromMinutes(-1));
        await RunDueJobAsync(id);
        await RunDueJobAsync(id);
        Assert.Equal(ExchangeStatus.Completed, await StatusAsync(id));
        await RunDueJobAsync(Guid.NewGuid());                        // неизвестный id — без ошибки
    }

    [Fact]
    public async Task The_job_leaves_frozen_and_aborted_exchanges_alone()
    {
        var (_, _, _, frozen) = await ExchangeAsync();
        var (_, a, _, aborted) = await ExchangeAsync();
        await SendAsync(a, HttpMethod.Post, $"/exchanges/{aborted}/abort", new { reason = "x" });
        await SetStatusAsync(frozen, ExchangeStatus.Frozen);
        await MoveEndDateAsync(frozen, TimeSpan.FromMinutes(-1));
        await MoveEndDateAsync(aborted, TimeSpan.FromMinutes(-1));

        await RunDueJobAsync(frozen);
        await RunDueJobAsync(aborted);

        Assert.Equal(ExchangeStatus.Frozen, await StatusAsync(frozen));   // замороженный по сроку не завершается
        Assert.Equal(ExchangeStatus.Aborted, await StatusAsync(aborted));
    }

    [Fact]
    public async Task The_old_job_does_nothing_after_the_exchange_was_extended()
    {
        var (_, a, b, id) = await ExchangeAsync();
        await SendAsync(a, HttpMethod.Post, $"/exchanges/{id}/extend");
        await SendAsync(b, HttpMethod.Post, $"/exchanges/{id}/extend");

        await RunDueJobAsync(id);   // сработала задача, поставленная на прежний срок: до нового срока ещё далеко

        Assert.Equal(ExchangeStatus.Active, await StatusAsync(id));
    }

    // ---------- доступ и списки ----------

    [Fact]
    public async Task A_stranger_sees_an_exchange_as_nonexistent()
    {
        var (world, _, _, id) = await ExchangeAsync();
        var stranger = await world.UserAsync(Beginner0, Beginner1);

        foreach (var (method, url) in new[]
        {
            (HttpMethod.Get, $"/exchanges/{id}"), (HttpMethod.Post, $"/exchanges/{id}/complete"),
            (HttpMethod.Post, $"/exchanges/{id}/abort"), (HttpMethod.Post, $"/exchanges/{id}/extend"),
            (HttpMethod.Get, $"/exchanges/{Guid.NewGuid()}"),
        })
        {
            var response = await SendAsync(stranger, method, url, method == HttpMethod.Post ? new { reason = "x" } : null);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("exchange_not_found", await CodeAsync(response));
        }

        Assert.Equal(ExchangeStatus.Active, await StatusAsync(id));
    }

    [Fact]
    public async Task Unauthenticated_requests_are_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/exchanges")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync($"/api/v1/exchanges/{Guid.NewGuid()}/complete", null)).StatusCode);
    }

    [Fact]
    public async Task The_list_shows_only_my_exchanges_newest_first_with_status_filter_and_paging()
    {
        var (world, busy, exchanges) = await BusyUserAsync(3);
        await SendAsync(busy, HttpMethod.Post, $"/exchanges/{exchanges[0]}/abort", new { reason = "x" });

        var all = await JsonAsync(await SendAsync(busy, HttpMethod.Get, "/exchanges"));
        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(exchanges[2], all.GetProperty("items")[0].GetProperty("id").GetGuid());

        var active = await JsonAsync(await SendAsync(busy, HttpMethod.Get, "/exchanges?status=Active"));
        Assert.Equal(2, active.GetProperty("totalCount").GetInt32());
        var paged = await JsonAsync(await SendAsync(busy, HttpMethod.Get, "/exchanges?pageSize=1&page=2"));
        Assert.Equal(1, paged.GetProperty("items").GetArrayLength());

        var stranger = await world.UserAsync(Beginner0, Beginner1);
        Assert.Equal(0, (await JsonAsync(await SendAsync(stranger, HttpMethod.Get, "/exchanges"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(busy, HttpMethod.Get, "/exchanges?pageSize=999")).StatusCode);
    }

    [Fact]
    public async Task Staff_accounts_have_no_exchanges()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Moderator);
        await world.ModifyAsync(db => { db.Users.Add(staff); return Task.CompletedTask; });

        var response = await SendAsync(new MatchPerson(staff, TestTokens.ForUser(_factory, staff)), HttpMethod.Get, "/exchanges");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3, 0, false)]
    [InlineData(0, 4, true)]
    [InlineData(7, 3, true)]
    [InlineData(5, 5, false)]
    public void The_balance_reminder_appears_when_the_difference_is_more_than_three(int mine, int partner, bool reminder)
    {
        var balance = ExchangeBalance.Calculate(mine, partner);

        Assert.Equal(reminder, balance.ShowReminder);
        Assert.Equal(mine, balance.MyLessons);
    }
}
