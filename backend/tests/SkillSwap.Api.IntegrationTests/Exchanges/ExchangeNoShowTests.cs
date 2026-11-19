using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>Выход из обмена после трёх окончательных неявок партнёра (раздел 6.3 ТЗ): порог, виновный, признак, уведомление, гонки.</summary>
[Collection(ApiCollection.Name)]
public class ExchangeNoShowTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ExchangeNoShowTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

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
        var created = await ApiAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        return (await JsonAsync(await ApiAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept"))).GetProperty("exchangeId").GetGuid();
    }

    /// <summary>Добавляет в обмен урок со статусом NoShow по вине <paramref name="culprit"/>; <paramref name="final"/> — итог уже подведён при закрытии комнаты.</summary>
    private async Task NoShowAsync(Guid exchangeId, Guid teacher, MatchPerson culprit, bool final = true, int daysAgo = 3)
    {
        await _factory.QueryAsync(async db =>
        {
            var start = Now.AddDays(-daysAgo);
            var session = LessonSession.Propose(exchangeId, teacher, teacher, Now.AddHours(5), null, Now, Now.AddDays(30));
            db.Add(session);
            await db.SaveChangesAsync();
            await db.Sessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.StartAt, start).SetProperty(x => x.EndAt, start.AddMinutes(45))
                .SetProperty(x => x.Status, SessionStatus.NoShow).SetProperty(x => x.NoShowUserId, culprit.Id)
                .SetProperty(x => x.RoomClosedAt, final ? start.AddMinutes(75) : (DateTime?)null));
            return 0;
        });
    }

    private async Task NoShowsAsync(Guid exchangeId, MatchPerson teacher, MatchPerson culprit, int count, bool final = true)
    {
        for (var i = 0; i < count; i++)
        {
            await NoShowAsync(exchangeId, teacher.Id, culprit, final, daysAgo: 3 + i);
        }
    }

    private Task<HttpResponseMessage> AbortForNoShowAsync(MatchPerson who, Guid exchangeId, string reason = "partner_no_show") =>
        ApiAsync(who, HttpMethod.Post, $"/exchanges/{exchangeId}/abort", new { reason });

    private async Task<JsonElement> ViewAsync(MatchPerson who, Guid exchangeId) =>
        await JsonAsync(await ApiAsync(who, HttpMethod.Get, $"/exchanges/{exchangeId}"));

    private async Task<List<JsonElement>> NotificationsOf(MatchPerson who, string type)
    {
        var list = (await JsonAsync(await ApiAsync(who, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return list.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    // ---------- порог ----------

    [Fact]
    public async Task Three_final_no_shows_of_the_partner_let_the_victim_leave_and_the_partner_is_recorded_as_the_one_who_left()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await NoShowsAsync(exchangeId, a, culprit: b, 3);

        var response = await AbortForNoShowAsync(a, exchangeId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mine = await JsonAsync(response);
        Assert.Equal("Aborted", mine.GetProperty("status").GetString());
        Assert.Equal("partner", mine.GetProperty("abortedBy").GetString());          // у пострадавшего виновный — партнёр
        Assert.Equal("partner_no_show", mine.GetProperty("abortReason").GetString());
        Assert.True(mine.GetProperty("abortedForNoShow").GetBoolean());
        var theirs = await ViewAsync(b, exchangeId);
        Assert.Equal("me", theirs.GetProperty("abortedBy").GetString());             // у неявившегося — он сам
        Assert.Equal("partner_no_show", theirs.GetProperty("abortReason").GetString());
        Assert.True(theirs.GetProperty("abortedForNoShow").GetBoolean());
        Assert.False(mine.GetProperty("canAbortForNoShow").GetBoolean());             // обмен закрыт
    }

    [Fact]
    public async Task The_mark_is_kept_in_the_database_for_the_one_sided_review_of_stage_six()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await NoShowsAsync(exchangeId, a, b, 3);

        await AbortForNoShowAsync(a, exchangeId);

        var row = await _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == exchangeId));
        Assert.True(row.AbortedForNoShow);
        Assert.Equal(b.Id, row.AbortedById);
        Assert.Equal(Exchange.AbortReasonPartnerNoShow, row.AbortReason);
        Assert.Equal(ExchangeStatus.Aborted, row.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Fewer_than_three_no_shows_is_a_conflict_with_the_numbers_and_the_exchange_stays_active(int count)
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await NoShowsAsync(exchangeId, a, b, count);

        var response = await AbortForNoShowAsync(a, exchangeId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("no_show_threshold_not_reached", body.GetProperty("code").GetString());
        Assert.Equal(count, body.GetProperty("partnerNoShows").GetInt32());
        Assert.Equal(3, body.GetProperty("required").GetInt32());
        Assert.Equal("Active", (await ViewAsync(a, exchangeId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_preliminary_no_show_does_not_count_until_the_room_is_closed()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await NoShowsAsync(exchangeId, a, b, 2);
        await NoShowsAsync(exchangeId, a, b, 3, final: false);   // на 15-й минуте: опоздавший ещё может зайти

        var view = await ViewAsync(a, exchangeId);

        Assert.Equal(2, view.GetProperty("partnerNoShows").GetInt32());
        Assert.False(view.GetProperty("canAbortForNoShow").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await AbortForNoShowAsync(a, exchangeId)).StatusCode);
    }

    [Fact]
    public async Task Only_the_no_shows_of_the_partner_count_not_my_own()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await NoShowsAsync(exchangeId, a, culprit: a, 3);   // не пришёл я сам

        Assert.Equal(HttpStatusCode.Conflict, (await AbortForNoShowAsync(a, exchangeId)).StatusCode);   // у партнёра неявок нет
        Assert.Equal(0, (await ViewAsync(a, exchangeId)).GetProperty("partnerNoShows").GetInt32());

        // А для партнёра это уже основание: он пострадавший.
        var view = await ViewAsync(b, exchangeId);
        Assert.Equal(3, view.GetProperty("partnerNoShows").GetInt32());
        Assert.True(view.GetProperty("canAbortForNoShow").GetBoolean());
    }

    [Fact]
    public async Task No_shows_of_the_same_person_in_another_exchange_do_not_count_here()
    {
        var (world, a, b, exchangeId) = await ExchangeAsync();
        var c = await world.UserAsync(Beginner0, Beginner1);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var otherExchange = await OpenExchangeAsync(world, c, b);
        await NoShowsAsync(otherExchange, c, culprit: b, 3);   // b не приходил к другому человеку

        Assert.Equal(HttpStatusCode.Conflict, (await AbortForNoShowAsync(a, exchangeId)).StatusCode);   // здесь за b ничего нет
        Assert.Equal(0, (await ViewAsync(a, exchangeId)).GetProperty("partnerNoShows").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await AbortForNoShowAsync(c, otherExchange)).StatusCode);       // а там основание есть
    }

    [Fact]
    public async Task The_reason_is_matched_without_regard_to_case_and_spaces_but_any_other_text_is_an_ordinary_exit()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await NoShowsAsync(exchangeId, a, b, 3);

        var ordinary = await AbortForNoShowAsync(b, exchangeId, "Мне не подходит время");

        // b — тот, кто не приходил, выходит обычным способом: виновный — он сам, признака неявок нет
        Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
        var view = await JsonAsync(ordinary);
        Assert.Equal("me", view.GetProperty("abortedBy").GetString());
        Assert.False(view.GetProperty("abortedForNoShow").GetBoolean());
        Assert.Equal("Мне не подходит время", view.GetProperty("abortReason").GetString());

        var (_, a2, b2, exchange2) = await ExchangeAsync();
        await NoShowsAsync(exchange2, a2, b2, 3);
        var upper = await AbortForNoShowAsync(a2, exchange2, "  Partner_No_Show ");
        Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        Assert.True((await JsonAsync(upper)).GetProperty("abortedForNoShow").GetBoolean());
    }

    // ---------- что видно участникам ----------

    [Fact]
    public async Task The_exchange_shows_the_count_and_when_leaving_is_possible_in_the_card_and_in_the_list()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await NoShowsAsync(exchangeId, a, b, 2);
        Assert.Equal(2, (await ViewAsync(a, exchangeId)).GetProperty("partnerNoShows").GetInt32());
        Assert.False((await ViewAsync(a, exchangeId)).GetProperty("canAbortForNoShow").GetBoolean());

        await NoShowAsync(exchangeId, a.Id, b);

        var card = await ViewAsync(a, exchangeId);
        Assert.Equal(3, card.GetProperty("partnerNoShows").GetInt32());
        Assert.True(card.GetProperty("canAbortForNoShow").GetBoolean());
        var list = await JsonAsync(await ApiAsync(a, HttpMethod.Get, "/exchanges"));
        var listed = list.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == exchangeId);
        Assert.Equal(3, listed.GetProperty("partnerNoShows").GetInt32());
        Assert.True(listed.GetProperty("canAbortForNoShow").GetBoolean());

        var theirs = await ViewAsync(b, exchangeId);
        Assert.Equal(0, theirs.GetProperty("partnerNoShows").GetInt32());   // для неявившегося у «партнёра» неявок нет
        Assert.False(theirs.GetProperty("canAbortForNoShow").GetBoolean());
    }

    [Fact]
    public async Task The_partner_gets_the_usual_exit_notification_with_the_no_show_reason_and_open_lessons_are_cancelled_without_a_culprit()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await NoShowsAsync(exchangeId, a, b, 3);
        var proposed = await ApiAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { startAt = Now.AddDays(2).ToString("O"), teacherId = a.Id });
        var openId = (await JsonAsync(proposed)).GetProperty("id").GetGuid();

        await AbortForNoShowAsync(a, exchangeId);

        var n = Assert.Single(await NotificationsOf(b, "ExchangeAborted"));
        Assert.Equal("partner_no_show", n.GetProperty("payload").GetProperty("reason").GetString());
        Assert.Equal(a.Id, n.GetProperty("payload").GetProperty("userId").GetGuid());
        var session = await _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == openId));
        Assert.Equal(SessionStatus.Cancelled, session.Status);
        Assert.Null(session.CancelledById);
    }

    // ---------- состояния и гонки ----------

    [Fact]
    public async Task A_closed_exchange_cannot_be_left_again_and_two_simultaneous_exits_make_one_winner()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await NoShowsAsync(exchangeId, a, b, 3);

        var responses = await Task.WhenAll(
            AbortForNoShowAsync(a, exchangeId),
            AbortForNoShowAsync(a, exchangeId),
            AbortForNoShowAsync(a, exchangeId));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(HttpStatusCode.Conflict, (await AbortForNoShowAsync(a, exchangeId)).StatusCode);
        Assert.Single(await NotificationsOf(b, "ExchangeAborted"));
    }

    [Fact]
    public async Task A_frozen_exchange_cannot_be_left_this_way()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await NoShowsAsync(exchangeId, a, b, 3);
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExchangeStatus.Frozen));
            return 0;
        });

        var response = await AbortForNoShowAsync(a, exchangeId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False((await ViewAsync(a, exchangeId)).GetProperty("canAbortForNoShow").GetBoolean());
    }

    [Fact]
    public async Task A_stranger_gets_nothing_and_an_unknown_exchange_is_not_found()
    {
        var (world, a, b, exchangeId) = await ExchangeAsync();
        await NoShowsAsync(exchangeId, a, b, 3);
        var stranger = await world.UserAsync(Beginner0, Beginner1);

        Assert.Equal(HttpStatusCode.NotFound, (await AbortForNoShowAsync(stranger, exchangeId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AbortForNoShowAsync(a, Guid.NewGuid())).StatusCode);
        Assert.Equal("Active", (await ViewAsync(a, exchangeId)).GetProperty("status").GetString());
    }
}
