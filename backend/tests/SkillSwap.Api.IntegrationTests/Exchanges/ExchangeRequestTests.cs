using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>Заявки на обмен (разделы 5.3, 9.1 и 12 ТЗ): отправка, ответы, лимиты, истечение, гонки.</summary>
[Collection(ApiCollection.Name)]
public class ExchangeRequestTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ExchangeRequestTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    /// <summary>Двое: A умеет навык 0 и хочет навык 1, B умеет навык 1 и хочет навык 0.</summary>
    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B)> PairAsync()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        return (world, a, b);
    }

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

    private Task<HttpResponseMessage> SendRequestAsync(MatchPerson from, MatchPerson to, Guid toSkill, Guid? fromSkill, int days = 30, string? message = null) =>
        SendAsync(from, HttpMethod.Post, "/exchange-requests", new
        {
            toUserId = to.Id, toTeachesSkillId = toSkill, fromTeachesSkillId = fromSkill, durationDays = days, message,
        });

    private async Task<Guid> CreateAsync(MatchPerson from, MatchPerson to, Guid toSkill, Guid? fromSkill)
    {
        var response = await SendRequestAsync(from, to, toSkill, fromSkill);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<ExchangeRequestStatus> StatusAsync(Guid id) =>
        await _factory.QueryAsync(db => db.ExchangeRequests.Where(r => r.Id == id).Select(r => r.Status).SingleAsync());

    // ---------- отправка ----------

    [Fact]
    public async Task A_request_is_created_pending_and_both_sides_see_it_in_their_boxes()
    {
        var (w, a, b) = await PairAsync();

        var response = await SendRequestAsync(a, b, w.S(1), w.S(0), 60, "  Привет!  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await JsonAsync(response);
        Assert.Equal("Pending", created.GetProperty("status").GetString());
        Assert.Equal("out", created.GetProperty("direction").GetString());
        Assert.Equal(60, created.GetProperty("durationDays").GetInt32());
        Assert.Equal("Привет!", created.GetProperty("message").GetString());   // пробелы по краям убраны
        Assert.Equal(w.S(0), created.GetProperty("fromTeachesSkill").GetProperty("id").GetGuid());
        Assert.Equal(w.S(1), created.GetProperty("toTeachesSkill").GetProperty("id").GetGuid());
        var id = created.GetProperty("id").GetGuid();
        Assert.Contains($"/exchange-requests/{id}", response.Headers.Location!.ToString());

        var outbox = await JsonAsync(await SendAsync(a, HttpMethod.Get, "/exchange-requests?box=out"));
        Assert.Contains(outbox.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);
        var inbox = await JsonAsync(await SendAsync(b, HttpMethod.Get, "/exchange-requests?box=in"));
        var item = Assert.Single(inbox.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);
        Assert.Equal("in", item.GetProperty("direction").GetString());
        Assert.Equal(a.Id, item.GetProperty("fromUser").GetProperty("id").GetGuid());

        // И в чужом ящике этой заявки нет.
        var wrongBox = await JsonAsync(await SendAsync(a, HttpMethod.Get, "/exchange-requests?box=in"));
        Assert.DoesNotContain(wrongBox.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task A_request_without_the_senders_skill_is_allowed()
    {
        var (w, a, b) = await PairAsync();

        var response = await SendRequestAsync(a, b, w.S(1), null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(response)).GetProperty("fromTeachesSkill").ValueKind);
    }

    [Fact]
    public async Task Sending_schedules_the_expiry_job_for_seven_days_later()
    {
        var (w, a, b) = await PairAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();
        var before = jobs.Scheduled.Count;

        var response = await SendRequestAsync(a, b, w.S(1), w.S(0));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var scheduled = jobs.Scheduled.Skip(before).Where(j => j.Description.Contains(nameof(ExchangeRequestJobs.ExpireAsync))).ToList();
        var job = Assert.Single(scheduled);
        var expiresAt = (await JsonAsync(response)).GetProperty("expiresAt").GetDateTime();
        Assert.Equal(7, Math.Round((expiresAt - _factory.Clock.GetUtcNow().UtcDateTime).TotalDays));
        Assert.True(Math.Abs((expiresAt - job.RunAt.UtcDateTime).TotalMilliseconds) < 5);   // база хранит микросекунды
    }

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(90)]
    public async Task Duration_must_be_30_or_60(int days)
    {
        var (w, a, b) = await PairAsync();

        var response = await SendRequestAsync(a, b, w.S(1), w.S(0), days);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeAsync(response));
    }

    [Fact]
    public async Task A_message_over_1000_characters_is_refused()
    {
        var (w, a, b) = await PairAsync();

        var response = await SendRequestAsync(a, b, w.S(1), w.S(0), 30, new string('я', 1001));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_skill_must_be_taught_by_the_recipient_and_the_own_skill_by_the_sender()
    {
        var (w, a, b) = await PairAsync();

        var notTheirs = await SendRequestAsync(a, b, w.S(0), w.S(0));     // навык 0 получатель не преподаёт
        var notMine = await SendRequestAsync(a, b, w.S(1), w.S(2));       // навыка 2 нет у отправителя

        Assert.Equal(HttpStatusCode.BadRequest, notTheirs.StatusCode);
        Assert.Equal("skill_not_taught_by_recipient", await CodeAsync(notTheirs));
        Assert.Equal(HttpStatusCode.BadRequest, notMine.StatusCode);
        Assert.Equal("skill_not_taught_by_sender", await CodeAsync(notMine));
    }

    [Fact]
    public async Task A_request_to_oneself_is_refused()
    {
        var (w, a, _) = await PairAsync();

        var response = await SendRequestAsync(a, a, w.S(0), w.S(0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("cannot_request_self", await CodeAsync(response));
    }

    [Fact]
    public async Task An_incomplete_questionnaire_of_the_sender_blocks_requests()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var noLanguage = await world.UserAsync(Beginner0, Beginner1, new MatchTestWorld.Options(Languages: []));

        var response = await SendRequestAsync(noLanguage, b, world.S(1), world.S(0));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("skills_incomplete", await CodeAsync(response));
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("deleted")]
    [InlineData("blocked-by-recipient")]
    [InlineData("blocked-by-sender")]
    [InlineData("other-age-group")]
    [InlineData("unknown")]
    public async Task The_recipient_who_is_not_visible_does_not_exist_for_the_sender(string reason)
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(
            [Beginner1], [Beginner0],
            new MatchTestWorld.Options(
                Searchable: reason != "hidden",
                Status: reason == "deleted" ? UserStatus.Deleted : UserStatus.Active,
                BirthDate: reason == "other-age-group" ? DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-17)) : null));
        if (reason == "blocked-by-recipient")
        {
            await world.ModifyAsync(db => { db.UserBlocks.Add(UserBlock.Create(b.Id, a.Id, DateTime.UtcNow)); return Task.CompletedTask; });
        }

        if (reason == "blocked-by-sender")
        {
            await world.ModifyAsync(db => { db.UserBlocks.Add(UserBlock.Create(a.Id, b.Id, DateTime.UtcNow)); return Task.CompletedTask; });
        }

        var target = reason == "unknown" ? new MatchPerson(b.User, b.Token) : b;
        var response = reason == "unknown"
            ? await SendAsync(a, HttpMethod.Post, "/exchange-requests", new { toUserId = Guid.NewGuid(), toTeachesSkillId = world.S(1), durationDays = 30 })
            : await SendRequestAsync(a, target, world.S(1), world.S(0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("user_not_found", await CodeAsync(response));
    }

    [Fact]
    public async Task Unauthenticated_requests_are_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/exchange-requests?box=in")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsJsonAsync("/api/v1/exchange-requests", new { })).StatusCode);
    }

    // ---------- дубли, кулдаун, лимиты ----------

    [Fact]
    public async Task A_second_open_request_to_the_same_person_is_a_conflict_with_the_existing_id()
    {
        var (w, a, b) = await PairAsync();
        var first = await CreateAsync(a, b, w.S(1), w.S(0));

        var again = await SendRequestAsync(a, b, w.S(1), w.S(0));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await JsonAsync(again);
        Assert.Equal("request_already_pending", problem.GetProperty("code").GetString());
        Assert.Equal(first, problem.GetProperty("requestId").GetGuid());
    }

    [Fact]
    public async Task A_request_towards_someone_who_already_wrote_to_me_points_at_their_request()
    {
        var (w, a, b) = await PairAsync();
        var theirs = await CreateAsync(b, a, w.S(0), w.S(1));

        var reverse = await SendRequestAsync(a, b, w.S(1), w.S(0));

        Assert.Equal(HttpStatusCode.Conflict, reverse.StatusCode);
        var problem = await JsonAsync(reverse);
        Assert.Equal("reverse_request_pending", problem.GetProperty("code").GetString());
        Assert.Equal(theirs, problem.GetProperty("requestId").GetGuid());
    }

    [Fact]
    public async Task After_a_decline_the_sender_waits_30_days_and_then_may_write_again()
    {
        var (w, a, b) = await PairAsync();
        var id = await CreateAsync(a, b, w.S(1), w.S(0));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/decline")).StatusCode);

        var tooSoon = await SendRequestAsync(a, b, w.S(1), w.S(0));

        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);
        var problem = await JsonAsync(tooSoon);
        Assert.Equal("request_cooldown", problem.GetProperty("code").GetString());
        Assert.True(problem.TryGetProperty("retryAfter", out _));

        // Отказ «состарился» на 31 день.
        await _factory.QueryAsync(async db =>
        {
            await db.ExchangeRequests.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.RespondedAt, DateTime.UtcNow.AddDays(-31)));
            return 0;
        });
        Assert.Equal(HttpStatusCode.Created, (await SendRequestAsync(a, b, w.S(1), w.S(0))).StatusCode);

        // Кулдаун односторонний: тот, кто отказал, может написать первым когда угодно.
        var (w2, a2, b2) = await PairAsync();
        var id2 = await CreateAsync(a2, b2, w2.S(1), w2.S(0));
        await SendAsync(b2, HttpMethod.Post, $"/exchange-requests/{id2}/decline");
        Assert.Equal(HttpStatusCode.Created, (await SendRequestAsync(b2, a2, w2.S(0), w2.S(1))).StatusCode);
    }

    [Fact]
    public async Task Withdrawing_does_not_start_a_cooldown()
    {
        var (w, a, b) = await PairAsync();
        var id = await CreateAsync(a, b, w.S(1), w.S(0));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(a, HttpMethod.Post, $"/exchange-requests/{id}/withdraw")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await SendRequestAsync(a, b, w.S(1), w.S(0))).StatusCode);
    }

    [Fact]
    public async Task The_sixth_pending_outgoing_request_is_refused_and_a_freed_slot_can_be_reused()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var targets = new List<MatchPerson>();
        for (var i = 0; i < 6; i++)
        {
            targets.Add(await world.UserAsync(Beginner1, Beginner0));
        }

        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await CreateAsync(a, targets[i], world.S(1), world.S(0)));
        }

        var sixth = await SendRequestAsync(a, targets[5], world.S(1), world.S(0));
        Assert.Equal(HttpStatusCode.Conflict, sixth.StatusCode);
        Assert.Equal("too_many_pending_requests", await CodeAsync(sixth));

        await SendAsync(a, HttpMethod.Post, $"/exchange-requests/{ids[0]}/withdraw");
        Assert.Equal(HttpStatusCode.Created, (await SendRequestAsync(a, targets[5], world.S(1), world.S(0))).StatusCode);
    }

    [Fact]
    public async Task Requests_past_their_expiry_do_not_count_against_the_pending_limit()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var target = await world.UserAsync(Beginner1, Beginner0);
            ids.Add(await CreateAsync(a, target, world.S(1), world.S(0)));
        }

        // Срок первой вышел, а фоновая задача ещё не успела её закрыть.
        await _factory.QueryAsync(async db =>
        {
            await db.ExchangeRequests.Where(r => r.Id == ids[0]).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            return 0;
        });

        var extra = await world.UserAsync(Beginner1, Beginner0);
        Assert.Equal(HttpStatusCode.Created, (await SendRequestAsync(a, extra, world.S(1), world.S(0))).StatusCode);
    }

    [Fact]
    public async Task The_eleventh_request_within_a_day_is_refused_with_429_even_when_earlier_ones_are_closed()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        for (var i = 0; i < 10; i++)
        {
            var target = await world.UserAsync(Beginner1, Beginner0);
            var id = await CreateAsync(a, target, world.S(1), world.S(0));
            await SendAsync(a, HttpMethod.Post, $"/exchange-requests/{id}/withdraw");   // отозвать — слот в «ожидающих» свободен, но сутки считаются
        }

        var last = await world.UserAsync(Beginner1, Beginner0);
        var response = await SendRequestAsync(a, last, world.S(1), world.S(0));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("daily_request_limit", await CodeAsync(response));
    }

    [Fact]
    public async Task Limits_are_taken_from_system_settings()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var first = await world.UserAsync(Beginner1, Beginner0);
        var second = await world.UserAsync(Beginner1, Beginner0);
        try
        {
            await SetSettingAsync(SystemSettingKeys.MaxPendingOutgoing, "1");
            await CreateAsync(a, first, world.S(1), world.S(0));

            var refused = await SendRequestAsync(a, second, world.S(1), world.S(0));
            Assert.Equal("too_many_pending_requests", await CodeAsync(refused));
        }
        finally
        {
            await SetSettingAsync(SystemSettingKeys.MaxPendingOutgoing, "5");
        }
    }

    private Task SetSettingAsync(string key, string value) => _factory.QueryAsync(async db =>
    {
        await db.SystemSettings.Where(s => s.Key == key).ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, value));
        return 0;
    });

    // ---------- ответы ----------

    [Fact]
    public async Task The_recipient_accepts_a_request_that_names_both_skills()
    {
        var (w, a, b) = await PairAsync();
        var id = await CreateAsync(a, b, w.S(1), w.S(0));

        var response = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/accept");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accepted = await JsonAsync(response);
        Assert.Equal("Accepted", accepted.GetProperty("status").GetString());
        Assert.Equal("in", accepted.GetProperty("direction").GetString());
        Assert.NotEqual(JsonValueKind.Null, accepted.GetProperty("respondedAt").ValueKind);
    }

    [Fact]
    public async Task Without_the_senders_skill_the_recipient_must_choose_one_of_the_senders_teach_skills()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync([Beginner0, (2, SkillLevel.Beginner)], [Beginner1]);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var id = await CreateAsync(a, b, world.S(1), null);

        var none = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/accept");
        var wrong = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/accept", new { fromTeachesSkillId = world.S(3) });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("from_skill_required", await CodeAsync(none));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("skill_not_taught_by_sender", await CodeAsync(wrong));
        Assert.Equal(ExchangeRequestStatus.Pending, await StatusAsync(id));   // неудачные попытки заявку не трогают

        var ok = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/accept", new { fromTeachesSkillId = world.S(2) });

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var accepted = await JsonAsync(ok);
        Assert.Equal(world.S(2), accepted.GetProperty("fromTeachesSkill").GetProperty("id").GetGuid());   // выбор сохранён в заявке
    }

    [Fact]
    public async Task A_skill_named_by_the_sender_cannot_be_replaced_on_accept()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync([Beginner0, (2, SkillLevel.Beginner)], [Beginner1]);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var id = await CreateAsync(a, b, world.S(1), world.S(0));

        var replace = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/accept", new { fromTeachesSkillId = world.S(2) });
        Assert.Equal(HttpStatusCode.BadRequest, replace.StatusCode);
        Assert.Equal("from_skill_fixed", await CodeAsync(replace));

        var same = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/accept", new { fromTeachesSkillId = world.S(0) });   // то же самое — можно
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
    }

    [Fact]
    public async Task Only_the_recipient_accepts_or_declines_and_only_the_sender_withdraws()
    {
        var (w, a, b) = await PairAsync();
        var id = await CreateAsync(a, b, w.S(1), w.S(0));

        foreach (var (who, action, code) in new[]
        {
            (a, "accept", "not_request_recipient"), (a, "decline", "not_request_recipient"), (b, "withdraw", "not_request_sender"),
        })
        {
            var response = await SendAsync(who, HttpMethod.Post, $"/exchange-requests/{id}/{action}");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(code, await CodeAsync(response));
        }

        Assert.Equal(ExchangeRequestStatus.Pending, await StatusAsync(id));
    }

    [Fact]
    public async Task A_stranger_sees_someone_elses_request_as_nonexistent()
    {
        var (w, a, b) = await PairAsync();
        var id = await CreateAsync(a, b, w.S(1), w.S(0));
        var stranger = await w.UserAsync(Beginner0, Beginner1);

        foreach (var (method, url) in new[]
        {
            (HttpMethod.Get, $"/exchange-requests/{id}"), (HttpMethod.Post, $"/exchange-requests/{id}/accept"),
            (HttpMethod.Post, $"/exchange-requests/{id}/decline"), (HttpMethod.Post, $"/exchange-requests/{id}/withdraw"),
            (HttpMethod.Get, $"/exchange-requests/{Guid.NewGuid()}"),
        })
        {
            var response = await SendAsync(stranger, method, url);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("request_not_found", await CodeAsync(response));
        }

        Assert.Equal(ExchangeRequestStatus.Pending, await StatusAsync(id));
    }

    [Theory]
    [InlineData("accept", "Accepted")]
    [InlineData("decline", "Declined")]
    public async Task A_decided_request_cannot_be_decided_again(string first, string expected)
    {
        var (w, a, b) = await PairAsync();
        var id = await CreateAsync(a, b, w.S(1), w.S(0));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/{first}")).StatusCode);

        foreach (var action in new[] { "accept", "decline" })
        {
            var again = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/{action}");
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("invalid_state_transition", await CodeAsync(again));
        }

        var withdraw = await SendAsync(a, HttpMethod.Post, $"/exchange-requests/{id}/withdraw");
        Assert.Equal(HttpStatusCode.Conflict, withdraw.StatusCode);
        Assert.Equal(Enum.Parse<ExchangeRequestStatus>(expected), await StatusAsync(id));
    }

    [Fact]
    public async Task A_withdrawn_request_cannot_be_accepted()
    {
        var (w, a, b) = await PairAsync();
        var id = await CreateAsync(a, b, w.S(1), w.S(0));
        await SendAsync(a, HttpMethod.Post, $"/exchange-requests/{id}/withdraw");

        var response = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/accept");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ExchangeRequestStatus.Withdrawn, await StatusAsync(id));
    }

    [Fact]
    public async Task Accepting_after_the_deadline_marks_the_request_expired()
    {
        var (w, a, b) = await PairAsync();
        var id = await CreateAsync(a, b, w.S(1), w.S(0));
        await _factory.QueryAsync(async db =>
        {
            await db.ExchangeRequests.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            return 0;
        });

        var response = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/accept");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("request_expired", await CodeAsync(response));
        Assert.Equal(ExchangeRequestStatus.Expired, await StatusAsync(id));   // состояние сохранено, а не откатано
    }

    [Fact]
    public async Task Accepting_is_refused_when_the_recipient_stopped_teaching_the_skill()
    {
        var (w, a, b) = await PairAsync();
        var id = await CreateAsync(a, b, w.S(1), w.S(0));
        await w.ModifyAsync(async db =>
        {
            var skill = await db.UserSkills.SingleAsync(s => s.UserId == b.Id && s.SkillId == w.S(1) && s.Direction == SkillDirection.Teach);
            db.UserSkills.Remove(skill);
        });

        var response = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/accept");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("skill_no_longer_taught", await CodeAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/decline")).StatusCode);   // отклонить можно
    }

    // ---------- фоновое истечение ----------

    private async Task RunExpireJobAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ExchangeRequestJobs>().ExpireAsync(id);
    }

    [Fact]
    public async Task The_expiry_job_closes_only_requests_whose_time_has_come_and_is_idempotent()
    {
        var (w, a, b) = await PairAsync();
        var id = await CreateAsync(a, b, w.S(1), w.S(0));

        await RunExpireJobAsync(id);                                   // рано: срок не вышел
        Assert.Equal(ExchangeRequestStatus.Pending, await StatusAsync(id));

        await _factory.QueryAsync(async db =>
        {
            await db.ExchangeRequests.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            return 0;
        });
        await RunExpireJobAsync(id);
        await RunExpireJobAsync(id);                                   // повтор ничего не портит
        Assert.Equal(ExchangeRequestStatus.Expired, await StatusAsync(id));

        // Принятая и отозванная заявки задачей не трогаются, даже если срок формально вышел.
        var accepted = await CreateAsync(a, await w.UserAsync(Beginner1, Beginner0), w.S(1), w.S(0));
        await _factory.QueryAsync(async db =>
        {
            await db.ExchangeRequests.Where(r => r.Id == accepted).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ExchangeRequestStatus.Withdrawn).SetProperty(r => r.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            return 0;
        });
        await RunExpireJobAsync(accepted);
        Assert.Equal(ExchangeRequestStatus.Withdrawn, await StatusAsync(accepted));

        await RunExpireJobAsync(Guid.NewGuid());                       // неизвестный id — тоже без ошибки
    }

    // ---------- гонки ----------

    [Fact]
    public async Task Accept_and_withdraw_at_the_same_moment_end_in_exactly_one_outcome()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (w, a, b) = await PairAsync();
            var id = await CreateAsync(a, b, w.S(1), w.S(0));

            var accept = SendAsync(b, HttpMethod.Post, $"/exchange-requests/{id}/accept");
            var withdraw = SendAsync(a, HttpMethod.Post, $"/exchange-requests/{id}/withdraw");
            var results = await Task.WhenAll(accept, withdraw);

            Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
            var status = await StatusAsync(id);
            Assert.Equal(results[0].StatusCode == HttpStatusCode.OK ? ExchangeRequestStatus.Accepted : ExchangeRequestStatus.Withdrawn, status);
        }
    }

    [Fact]
    public async Task Twenty_parallel_sends_respect_the_pending_limit()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var targets = new List<MatchPerson>();
        for (var i = 0; i < 12; i++)
        {
            targets.Add(await world.UserAsync(Beginner1, Beginner0));
        }

        var results = await Task.WhenAll(targets.Select(t => SendRequestAsync(a, t, world.S(1), world.S(0))));

        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(5, await _factory.QueryAsync(db => db.ExchangeRequests.CountAsync(r => r.FromUserId == a.Id)));
    }

    [Fact]
    public async Task Two_parallel_sends_to_the_same_person_create_one_request()
    {
        var (w, a, b) = await PairAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => SendRequestAsync(a, b, w.S(1), w.S(0))));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    // ---------- списки ----------

    [Theory]
    [InlineData(null)]
    [InlineData("sideways")]
    public async Task The_box_parameter_is_required_and_checked(string? box)
    {
        var (_, a, _) = await PairAsync();

        var response = await SendAsync(a, HttpMethod.Get, "/exchange-requests" + (box is null ? "" : "?box=" + box));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_box", await CodeAsync(response));
    }

    [Fact]
    public async Task The_list_filters_by_status_pages_and_orders_newest_first()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var target = await world.UserAsync(Beginner1, Beginner0);
            ids.Add(await CreateAsync(a, target, world.S(1), world.S(0)));
            await Task.Delay(20);
        }

        await SendAsync(a, HttpMethod.Post, $"/exchange-requests/{ids[0]}/withdraw");

        var all = await JsonAsync(await SendAsync(a, HttpMethod.Get, "/exchange-requests?box=out&pageSize=2"));
        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, all.GetProperty("items").GetArrayLength());
        Assert.Equal(ids[2], all.GetProperty("items")[0].GetProperty("id").GetGuid());   // новые сверху

        var pending = await JsonAsync(await SendAsync(a, HttpMethod.Get, "/exchange-requests?box=out&status=Pending"));
        Assert.Equal(2, pending.GetProperty("totalCount").GetInt32());
        var withdrawn = await JsonAsync(await SendAsync(a, HttpMethod.Get, "/exchange-requests?box=out&status=Withdrawn"));
        Assert.Equal(ids[0], Assert.Single(withdrawn.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(a, HttpMethod.Get, "/exchange-requests?box=out&pageSize=500")).StatusCode);
    }

    [Fact]
    public async Task Staff_accounts_cannot_use_requests()
    {
        var (w, _, b) = await PairAsync();
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Moderator);
        await w.ModifyAsync(db => { db.Users.Add(staff); return Task.CompletedTask; });
        var person = new MatchPerson(staff, TestTokens.ForUser(_factory, staff));

        var response = await SendRequestAsync(person, b, w.S(1), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("regular_account_required", await CodeAsync(response));
    }
}
