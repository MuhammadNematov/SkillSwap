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

/// <summary>Блокировка закрывает заявки и обмены пары; люди с заявкой или обменом не попадают в подбор.</summary>
[Collection(ApiCollection.Name)]
public class BlockAndMatchEffectsTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public BlockAndMatchEffectsTests(ApiFactory factory)
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

    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B)> PairAsync()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        return (world, await world.UserAsync(Beginner0, Beginner1), await world.UserAsync(Beginner1, Beginner0));
    }


    private Task<ExchangeRequestStatus> RequestStatusAsync(Guid id) =>
        _factory.QueryAsync(db => db.ExchangeRequests.Where(r => r.Id == id).Select(r => r.Status).SingleAsync());

    private Task<Exchange> ExchangeOfAsync(Guid requestId) =>
        _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.RequestId == requestId));

    private async Task<Guid> ExchangeFromAToBAsync(MatchTestWorld world, MatchPerson a, MatchPerson b)
    {
        var requestId = await RequestToBAsync(world, a, b);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept", new { fromTeachesSkillId = world.S(0) })).StatusCode);
        return requestId;
    }

    private async Task<Guid> RequestToBAsync(MatchTestWorld world, MatchPerson a, MatchPerson b)
    {
        var response = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> BlockAsync(MatchPerson who, MatchPerson target) =>
        SendAsync(who, HttpMethod.Post, $"/users/{target.Id}/block");

    // ---------- блокировка и заявки ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Blocking_expires_a_pending_request_in_either_direction_of_the_block(bool senderBlocks)
    {
        var (world, a, b) = await PairAsync();
        var requestId = await RequestToBAsync(world, a, b);

        var response = senderBlocks ? await BlockAsync(a, b) : await BlockAsync(b, a);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ExchangeRequestStatus.Expired, await RequestStatusAsync(requestId));
        var accept = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");
        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);   // принять уже нельзя
    }

    [Fact]
    public async Task A_request_from_the_blocked_side_towards_the_blocker_is_expired_too()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync([Beginner0, Beginner1], [Beginner1, Beginner0]);   // A и B умеют и хотят одно и то же
        var b = await world.UserAsync([Beginner0, Beginner1], [Beginner1, Beginner0]);
        var response = await SendAsync(b, HttpMethod.Post, "/exchange-requests",
            new { toUserId = a.Id, toTeachesSkillId = world.S(0), fromTeachesSkillId = world.S(1), durationDays = 30 });
        var requestId = (await JsonAsync(response)).GetProperty("id").GetGuid();

        await BlockAsync(b, a);   // кто отправил, тот и блокирует

        Assert.Equal(ExchangeRequestStatus.Expired, await RequestStatusAsync(requestId));
    }

    [Fact]
    public async Task Blocking_leaves_requests_of_other_pairs_and_closed_ones_alone()
    {
        var (world, a, b) = await PairAsync();
        var other = await world.UserAsync(Beginner1, Beginner0);
        var toOther = await RequestToBAsync(world, a, other);
        var declined = await RequestToBAsync(world, a, b);
        await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{declined}/decline");

        await BlockAsync(a, b);

        Assert.Equal(ExchangeRequestStatus.Pending, await RequestStatusAsync(toOther));
        Assert.Equal(ExchangeRequestStatus.Declined, await RequestStatusAsync(declined));   // закрытая заявка не меняется
    }

    // ---------- блокировка и обмены ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Blocking_aborts_an_active_exchange_with_the_reason_blocked_and_no_culprit(bool senderBlocks)
    {
        var (world, a, b) = await PairAsync();
        var requestId = await ExchangeFromAToBAsync(world, a, b);
        var exchange = await ExchangeOfAsync(requestId);

        await (senderBlocks ? BlockAsync(a, b) : BlockAsync(b, a));

        var closed = await ExchangeOfAsync(requestId);
        Assert.Equal(ExchangeStatus.Aborted, closed.Status);
        Assert.Null(closed.AbortedById);
        Assert.Equal(Exchange.AbortReasonBlocked, closed.AbortReason);   // в базе настоящая причина
        Assert.Equal(senderBlocks ? a.Id : b.Id, closed.BlockedById);
        Assert.NotNull(closed.ClosedAt);

        // Участники видят обмен прерванным, виновного нет.
        foreach (var person in new[] { a, b })
        {
            var view = await JsonAsync(await SendAsync(person, HttpMethod.Get, $"/exchanges/{exchange.Id}"));
            Assert.Equal("Aborted", view.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, view.GetProperty("abortedBy").ValueKind);
            // Блокировавший видит настоящую причину, заблокированный — нейтральную: факт блокировки ему не раскрывается.
            var blocker = senderBlocks ? a : b;
            Assert.Equal(person.Id == blocker.Id ? "blocked" : "partner_unavailable", view.GetProperty("abortReason").GetString());
        }

        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchange.Id}/complete")).StatusCode);
    }

    [Fact]
    public async Task Blocking_does_not_touch_frozen_or_finished_exchanges_or_other_partners()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var frozenPartner = await world.UserAsync(Beginner1, Beginner0);
        var otherPartner = await world.UserAsync(Beginner1, Beginner0);
        var frozen = await ExchangeFromAToBAsync(world, a, frozenPartner);
        var other = await ExchangeFromAToBAsync(world, a, otherPartner);
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.RequestId == frozen).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExchangeStatus.Frozen));
            return 0;
        });

        await BlockAsync(a, frozenPartner);
        await BlockAsync(a, otherPartner);

        Assert.Equal(ExchangeStatus.Frozen, (await ExchangeOfAsync(frozen)).Status);   // разбирательство модератора не прерывается
        Assert.Equal(ExchangeStatus.Aborted, (await ExchangeOfAsync(other)).Status);
    }

    [Fact]
    public async Task Blocking_twice_is_harmless_and_unblocking_does_not_restore_anything()
    {
        var (world, a, b) = await PairAsync();
        var requestId = await ExchangeFromAToBAsync(world, a, b);

        Assert.Equal(HttpStatusCode.NoContent, (await BlockAsync(a, b)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await BlockAsync(a, b)).StatusCode);
        await SendAsync(a, HttpMethod.Delete, $"/users/{b.Id}/block");

        Assert.Equal(ExchangeStatus.Aborted, (await ExchangeOfAsync(requestId)).Status);
    }

    [Fact]
    public async Task A_refused_block_changes_nothing_in_requests_and_exchanges()
    {
        var (world, a, b) = await PairAsync();
        var requestId = await RequestToBAsync(world, a, b);
        await _factory.QueryAsync(async db =>
        {
            for (var i = 0; i < UserBlock.MaxPerUser; i++)
            {
                var stranger = User.CreateRegular(AuthApi.NewEmail(), "Block", new DateOnly(1990, 1, 1), "en");
                db.Users.Add(stranger);
                db.UserBlocks.Add(UserBlock.Create(a.Id, stranger.Id, DateTime.UtcNow));
            }

            await db.SaveChangesAsync();
            return 0;
        });

        var response = await BlockAsync(a, b);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);   // too_many_blocks
        Assert.Equal(ExchangeRequestStatus.Pending, await RequestStatusAsync(requestId));
    }

    [Fact]
    public async Task Blocking_and_accepting_at_the_same_moment_never_leave_an_active_exchange()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (world, a, b) = await PairAsync();
            var requestId = await RequestToBAsync(world, a, b);

            var accept = SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");
            var block = BlockAsync(a, b);
            await Task.WhenAll(accept, block);

            Assert.Equal(HttpStatusCode.NoContent, block.Result.StatusCode);
            Assert.NotEqual(ExchangeRequestStatus.Pending, await RequestStatusAsync(requestId));
            Assert.False(await _factory.QueryAsync(db => db.Exchanges.AnyAsync(e =>
                e.RequestId == requestId && (e.Status == ExchangeStatus.Active))));
        }
    }

    // ---------- подбор ----------

    private async Task<bool> SeesAsync(MatchTestWorld world, MatchPerson viewer, MatchPerson other) => (await world.IdsAsync(viewer)).Contains(other.Id);

    [Fact]
    public async Task A_pending_request_hides_the_pair_from_matching_in_both_directions_until_it_is_closed()
    {
        var (world, a, b) = await PairAsync();
        Assert.True(await SeesAsync(world, a, b));

        var requestId = await RequestToBAsync(world, a, b);

        Assert.False(await SeesAsync(world, a, b));   // отправитель больше не видит получателя
        Assert.False(await SeesAsync(world, b, a));   // и получатель — отправителя

        await SendAsync(a, HttpMethod.Post, $"/exchange-requests/{requestId}/withdraw");

        Assert.True(await SeesAsync(world, a, b));
        Assert.True(await SeesAsync(world, b, a));
    }

    [Fact]
    public async Task A_declined_request_brings_the_person_back_into_matching()
    {
        var (world, a, b) = await PairAsync();
        var requestId = await RequestToBAsync(world, a, b);
        await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/decline");

        Assert.True(await SeesAsync(world, a, b));
    }

    [Fact]
    public async Task A_request_past_its_expiry_does_not_hide_anyone()
    {
        var (world, a, b) = await PairAsync();
        var requestId = await RequestToBAsync(world, a, b);
        await _factory.QueryAsync(async db =>
        {
            await db.ExchangeRequests.Where(r => r.Id == requestId).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            return 0;
        });

        Assert.True(await SeesAsync(world, a, b));
    }

    [Theory]
    [InlineData(ExchangeStatus.Active, false)]
    [InlineData(ExchangeStatus.Frozen, false)]
    [InlineData(ExchangeStatus.Completed, true)]
    [InlineData(ExchangeStatus.Aborted, true)]
    public async Task An_active_or_frozen_exchange_hides_the_partner_but_a_finished_one_does_not(ExchangeStatus status, bool visible)
    {
        var (world, a, b) = await PairAsync();
        var requestId = await ExchangeFromAToBAsync(world, a, b);
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.RequestId == requestId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, status));
            return 0;
        });

        Assert.Equal(visible, await SeesAsync(world, a, b));
        Assert.Equal(visible, await SeesAsync(world, b, a));
    }

    [Fact]
    public async Task Hiding_is_per_pair_other_candidates_stay_in_the_list()
    {
        var (world, a, b) = await PairAsync();
        var other = await world.UserAsync(Beginner1, Beginner0);
        await RequestToBAsync(world, a, b);

        var ids = await world.IdsAsync(a);

        Assert.DoesNotContain(b.Id, ids);
        Assert.Contains(other.Id, ids);
    }
}
