using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>
/// Работа модератора (разделы 3, 8.2 и 10.2 ТЗ) на настоящем приложении и БД: очередь, взятие в работу, конфликт интересов, доступ к чату, аудит, отзыв доступа и защита от IDOR.
/// </summary>
[Collection(ApiCollection.Name)]
public class ModeratorCasesTests
{
    private const string Complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ModeratorCasesTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private async Task<HttpResponseMessage> SendAsync(string? token, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    private Task<StaffPerson> ModeratorAsync(MatchPerson? linked = null) => ModerationTestData.StaffAsync(_factory, UserRole.Moderator, linked);

    private Task<(MatchTestWorld World, MatchPerson A, MatchPerson B, Guid ExchangeId)> NewExchangeAsync() => ModerationTestData.ExchangeAsync(_factory, _client);

    /// <summary>Жалоба на поведение, поданная через настоящий API; возвращает id обращения.</summary>
    private async Task<Guid> FileAsync(MatchPerson who, Guid exchangeId, object? messageIds = null)
    {
        var response = await SendAsync(who.Token, HttpMethod.Post, "/cases",
            new { type = "Conduct", category = "Insults", exchangeId, description = Complaint, messageIds });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<List<JsonElement>> NotificationsAsync(MatchPerson who, string type)
    {
        var items = (await JsonAsync(await SendAsync(who.Token, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return items.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    private async Task<int> NotificationCountAsync(MatchPerson who) =>
        (await JsonAsync(await SendAsync(who.Token, HttpMethod.Get, "/notifications?pageSize=1"))).GetProperty("totalCount").GetInt32();

    private Task<int> AuditCountAsync(Guid actor, string action, Guid? target = null) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.ActorId == actor && a.Action == action && (target == null || a.TargetId == target)));

    private Task<ChatAccessGrant[]> GrantsAsync(Guid caseId) => _factory.QueryAsync(db => db.ChatAccessGrants.AsNoTracking().Where(g => g.CaseId == caseId).ToArrayAsync());

    private Task<Case> CaseAsync(Guid id) => _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task<HttpResponseMessage> TakeAsync(StaffPerson moderator, Guid caseId) => SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/take");

    private Task<HttpResponseMessage> ChatAsync(string token, Guid caseId, string query = "") => SendAsync(token, HttpMethod.Get, $"/moderation/cases/{caseId}/chat{query}");

    /// <summary>Вся очередь (с учётом страниц): общая БД между тестами, поэтому ищем свои обращения среди всех.</summary>
    private async Task<List<JsonElement>> QueueAsync(StaffPerson moderator, string extra = "")
    {
        var all = new List<JsonElement>();
        for (var page = 1; ; page++)
        {
            var body = await JsonAsync(await SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases?page={page}&pageSize=50{extra}"));
            all.AddRange(body.GetProperty("items").EnumerateArray());
            if (all.Count >= body.GetProperty("totalCount").GetInt32() || body.GetProperty("items").GetArrayLength() == 0)
            {
                return all;
            }
        }
    }

    /// <summary>Обмен C → B, где B — партнёр A по первому обмену: так B становится «бывшим партнёром» A. Возвращает C и id обмена.</summary>
    private async Task<(MatchPerson C, Guid ExchangeId)> ExchangeWithPartnerAsync(MatchTestWorld world, MatchPerson b)
    {
        var c = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var created = await SendAsync(c.Token, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var accepted = await SendAsync(b.Token, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");
        return (c, (await JsonAsync(accepted)).GetProperty("exchangeId").GetGuid());
    }

    // ---------- роли ----------

    [Fact]
    public async Task Only_a_moderator_can_use_the_moderation_endpoints()
    {
        var x = await NewExchangeAsync();
        var caseId = await FileAsync(x.A, x.ExchangeId);
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var superAdmin = await ModerationTestData.StaffAsync(_factory, UserRole.SuperAdmin);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/moderation/cases")).StatusCode);
        foreach (var token in new[] { x.A.Token, x.B.Token, admin.Token, superAdmin.Token })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Get, "/moderation/cases")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Get, $"/moderation/cases/{caseId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Post, $"/moderation/cases/{caseId}/take")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ChatAsync(token, caseId)).StatusCode);
        }

        Assert.Equal(CaseStatus.Open, (await CaseAsync(caseId)).Status);
        Assert.Empty(await GrantsAsync(caseId));
    }

    [Fact]
    public async Task A_blocked_moderator_is_refused_even_with_a_valid_token()
    {
        var moderator = await ModeratorAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == moderator.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Blocked));
            return 0;
        });

        var response = await SendAsync(moderator.Token, HttpMethod.Get, "/moderation/cases");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- очередь ----------

    [Fact]
    public async Task The_queue_puts_high_priority_first_then_the_nearest_deadline_and_marks_overdue()
    {
        var moderator = await ModeratorAsync();
        var x = await NewExchangeAsync();
        var normal = (await JsonAsync(await SendAsync(x.A.Token, HttpMethod.Post, "/cases",
            new { type = "NoShowAbuse", category = "SystematicEvasion", exchangeId = x.ExchangeId, description = Complaint }))).GetProperty("id").GetGuid();
        var high = await FileAsync(x.A, x.ExchangeId);
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == normal).ExecuteUpdateAsync(s => s.SetProperty(c => c.DueAt, Now.AddHours(-1)));
            return 0;
        });

        var queue = await QueueAsync(moderator);
        var ids = queue.Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.True(ids.IndexOf(high) < ids.IndexOf(normal));   // высокий приоритет раньше, хотя срок у обычного ближе
        var normalItem = queue.Single(i => i.GetProperty("id").GetGuid() == normal);
        Assert.True(normalItem.GetProperty("isOverdue").GetBoolean());
        Assert.False(queue.Single(i => i.GetProperty("id").GetGuid() == high).GetProperty("isOverdue").GetBoolean());
        Assert.All(queue, i => Assert.Equal("Open", i.GetProperty("status").GetString()));

        var onlyNormal = await QueueAsync(moderator, "&priority=Normal");
        Assert.All(onlyNormal, i => Assert.Equal("Normal", i.GetProperty("priority").GetString()));
        Assert.Contains(onlyNormal, i => i.GetProperty("id").GetGuid() == normal);
        Assert.DoesNotContain(onlyNormal, i => i.GetProperty("id").GetGuid() == high);
    }

    [Theory]
    [InlineData("/moderation/cases?page=0")]
    [InlineData("/moderation/cases?pageSize=500")]
    public async Task Bad_paging_in_the_queue_is_a_clear_error(string url)
    {
        var moderator = await ModeratorAsync();

        var response = await SendAsync(moderator.Token, HttpMethod.Get, url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_paging", await CodeAsync(response));
    }

    [Fact]
    public async Task A_taken_case_leaves_the_queue_and_shows_up_in_my_cases_only_for_its_moderator()
    {
        var x = await NewExchangeAsync();
        var first = await ModeratorAsync();
        var second = await ModeratorAsync();
        var caseId = await FileAsync(x.A, x.ExchangeId);
        Assert.Equal(HttpStatusCode.OK, (await TakeAsync(first, caseId)).StatusCode);

        Assert.DoesNotContain(await QueueAsync(second), i => i.GetProperty("id").GetGuid() == caseId);
        Assert.Contains(await QueueAsync(first, "&scope=Mine"), i => i.GetProperty("id").GetGuid() == caseId);
        Assert.DoesNotContain(await QueueAsync(second, "&scope=Mine"), i => i.GetProperty("id").GetGuid() == caseId);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(first.Token, HttpMethod.Get, $"/moderation/cases/{caseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(second.Token, HttpMethod.Get, $"/moderation/cases/{caseId}")).StatusCode);
    }

    [Fact]
    public async Task The_card_of_a_dispute_carries_the_disputed_review()
    {
        var x = await NewExchangeAsync();
        var moderator = await ModeratorAsync();
        var review = await _factory.QueryAsync(async db =>
        {
            var r = Review.Create(x.ExchangeId, x.B.Id, x.A.Id, 1, 2, 1, 2, "Очень слабый урок, ничего не объяснил и опоздал.", false, Now);
            r.Publish(Now);
            db.Reviews.Add(r);
            await db.SaveChangesAsync();
            return r.Id;
        });
        var opened = await SendAsync(x.A.Token, HttpMethod.Post, "/cases",
            new { type = "ReviewDispute", category = "ReviewUnfair", exchangeId = x.ExchangeId, reviewId = review, description = Complaint });
        var caseId = (await JsonAsync(opened)).GetProperty("id").GetGuid();

        var card = await JsonAsync(await SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{caseId}"));

        var r = card.GetProperty("review");
        Assert.Equal(review, r.GetProperty("id").GetGuid());
        Assert.Equal("Frozen", r.GetProperty("status").GetString());
        Assert.Contains("опоздал", r.GetProperty("text").GetString());
        Assert.False(card.GetProperty("hasChatAccess").GetBoolean());
    }

    // ---------- взятие в работу ----------

    [Fact]
    public async Task Taking_a_case_gives_chat_access_to_that_exchange_audits_it_and_notifies_each_side_exactly_once()
    {
        var x = await NewExchangeAsync();
        var moderator = await ModeratorAsync();
        var caseId = await FileAsync(x.A, x.ExchangeId);
        Assert.Empty(await NotificationsAsync(x.B, "CaseTaken"));   // при подаче обвиняемый ничего не получает

        var response = await TakeAsync(moderator, caseId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("InProgress", dto.GetProperty("status").GetString());
        Assert.True(dto.GetProperty("isMine").GetBoolean());
        Assert.True(dto.GetProperty("hasChatAccess").GetBoolean());
        Assert.Equal(moderator.Id, dto.GetProperty("moderatorId").GetGuid());

        var grant = Assert.Single(await GrantsAsync(caseId));
        Assert.Equal(moderator.Id, grant.ModeratorId);
        Assert.Equal(x.ExchangeId, grant.ExchangeId);
        Assert.Equal(GrantReason.Case, grant.Reason);
        Assert.Null(grant.RevokedAt);
        Assert.Equal(1, await AuditCountAsync(moderator.Id, AuditActions.CaseTaken, caseId));
        Assert.Equal(1, await AuditCountAsync(moderator.Id, AuditActions.ChatAccessGranted, x.ExchangeId));

        var forOpener = Assert.Single(await NotificationsAsync(x.A, "CaseTaken"));
        var forAccused = Assert.Single(await NotificationsAsync(x.B, "CaseTaken"));
        Assert.Equal("Opener", forOpener.GetProperty("payload").GetProperty("role").GetString());
        Assert.Equal("Against", forAccused.GetProperty("payload").GetProperty("role").GetString());
        Assert.Equal(caseId, forAccused.GetProperty("payload").GetProperty("caseId").GetGuid());

        // Обвиняемый теперь видит обращение.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(x.B.Token, HttpMethod.Get, $"/cases/{caseId}")).StatusCode);
    }

    [Fact]
    public async Task A_case_can_be_taken_only_once_and_a_repeat_creates_nothing_new()
    {
        var x = await NewExchangeAsync();
        var first = await ModeratorAsync();
        var second = await ModeratorAsync();
        var caseId = await FileAsync(x.A, x.ExchangeId);
        Assert.Equal(HttpStatusCode.OK, (await TakeAsync(first, caseId)).StatusCode);

        var again = await TakeAsync(first, caseId);
        var other = await TakeAsync(second, caseId);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(other));
        Assert.Single(await GrantsAsync(caseId));
        Assert.Single(await NotificationsAsync(x.A, "CaseTaken"));
        Assert.Single(await NotificationsAsync(x.B, "CaseTaken"));
        Assert.Equal(first.Id, (await CaseAsync(caseId)).ModeratorId);
    }

    [Fact]
    public async Task Two_moderators_taking_at_the_same_time_leave_exactly_one_owner()
    {
        var x = await NewExchangeAsync();
        var first = await ModeratorAsync();
        var second = await ModeratorAsync();
        var caseId = await FileAsync(x.A, x.ExchangeId);

        var results = await Task.WhenAll(TakeAsync(first, caseId), TakeAsync(second, caseId));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Single(await GrantsAsync(caseId));
        Assert.Single(await NotificationsAsync(x.A, "CaseTaken"));
    }

    [Fact]
    public async Task A_withdrawn_case_cannot_be_taken_and_an_unknown_one_is_not_found()
    {
        var x = await NewExchangeAsync();
        var moderator = await ModeratorAsync();
        var caseId = await FileAsync(x.A, x.ExchangeId);
        await SendAsync(x.A.Token, HttpMethod.Post, $"/cases/{caseId}/withdraw");

        var withdrawn = await TakeAsync(moderator, caseId);
        var unknown = await TakeAsync(moderator, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, withdrawn.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(await GrantsAsync(caseId));
    }

    // ---------- конфликт интересов ----------

    [Fact]
    public async Task A_moderator_cannot_take_a_case_where_their_personal_account_takes_part()
    {
        var x = await NewExchangeAsync();
        var moderator = await ModeratorAsync(linked: x.A);   // личный аккаунт модератора — заявитель
        var caseId = await FileAsync(x.A, x.ExchangeId);

        var response = await TakeAsync(moderator, caseId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("conflict_of_interest", await CodeAsync(response));
        await AssertUntouchedAsync(caseId, x.A, x.B, moderator);
    }

    [Fact]
    public async Task A_moderator_cannot_take_a_case_against_their_personal_account_either()
    {
        var x = await NewExchangeAsync();
        var moderator = await ModeratorAsync(linked: x.B);   // личный аккаунт — обвиняемый
        var caseId = await FileAsync(x.A, x.ExchangeId);

        var response = await TakeAsync(moderator, caseId);

        Assert.Equal("conflict_of_interest", await CodeAsync(response));
        await AssertUntouchedAsync(caseId, x.A, x.B, moderator);
    }

    [Fact]
    public async Task A_moderator_cannot_take_a_case_where_a_former_exchange_partner_of_their_personal_account_takes_part()
    {
        var x = await NewExchangeAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == x.ExchangeId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExchangeStatus.Completed).SetProperty(e => e.ClosedAt, Now.AddDays(-40)));   // обмен давно закрыт
            return 0;
        });
        var moderator = await ModeratorAsync(linked: x.A);
        var (c, otherExchange) = await ExchangeWithPartnerAsync(x.World, x.B);   // B был партнёром A; теперь C жалуется на B
        var caseId = await FileAsync(c, otherExchange);

        var response = await TakeAsync(moderator, caseId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("conflict_of_interest", await CodeAsync(response));
        await AssertUntouchedAsync(caseId, c, x.B, moderator);
    }

    [Fact]
    public async Task A_case_with_a_conflict_is_hidden_from_that_moderators_queue_and_card_but_not_from_others()
    {
        var x = await NewExchangeAsync();
        var conflicted = await ModeratorAsync(linked: x.A);
        var other = await ModeratorAsync();
        var linkedWithoutRelation = await ModeratorAsync(linked: await x.World.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner)));
        var caseId = await FileAsync(x.B, x.ExchangeId);

        Assert.DoesNotContain(await QueueAsync(conflicted), i => i.GetProperty("id").GetGuid() == caseId);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(conflicted.Token, HttpMethod.Get, $"/moderation/cases/{caseId}")).StatusCode);
        Assert.Contains(await QueueAsync(other), i => i.GetProperty("id").GetGuid() == caseId);
        Assert.Contains(await QueueAsync(linkedWithoutRelation), i => i.GetProperty("id").GetGuid() == caseId);   // чужая привязка конфликта не создаёт
        Assert.Equal(HttpStatusCode.OK, (await TakeAsync(linkedWithoutRelation, caseId)).StatusCode);
    }

    /// <summary>Отказ по конфликту ничего не меняет: обращение не взято, доступа нет, уведомлений нет.</summary>
    private async Task AssertUntouchedAsync(Guid caseId, MatchPerson opener, MatchPerson accused, StaffPerson moderator)
    {
        var stored = await CaseAsync(caseId);
        Assert.Equal(CaseStatus.Open, stored.Status);
        Assert.Null(stored.ModeratorId);
        Assert.Empty(await GrantsAsync(caseId));
        Assert.Empty(await NotificationsAsync(opener, "CaseTaken"));
        Assert.Empty(await NotificationsAsync(accused, "CaseTaken"));
        Assert.Equal(0, await AuditCountAsync(moderator.Id, AuditActions.CaseTaken));
    }

    // ---------- чтение чата ----------

    /// <summary>Обмен с четырьмя сообщениями: обычное, исправленное, удалённое и доказательство.</summary>
    private async Task<(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid CaseId, Guid Plain, Guid Edited, Guid Deleted)> ChatCaseAsync()
    {
        var x = await NewExchangeAsync();
        var plain = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Обычное сообщение");
        var edited = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Первоначальная грубость");
        var deleted = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Угроза, которую потом стёрли");
        using (var scope = _factory.Services.CreateScope())
        {
            var cipher = scope.ServiceProvider.GetRequiredService<IMessageCipher>();
            await _factory.QueryAsync(async db =>
            {
                var m2 = await db.Messages.SingleAsync(m => m.Id == edited);
                db.MessageRevisions.Add(m2.Edit(x.B.Id, cipher.Encrypt("Исправленный вежливый текст"), false, true, m2.CreatedAt.AddMinutes(1))!);
                var m3 = await db.Messages.SingleAsync(m => m.Id == deleted);
                db.MessageRevisions.Add(m3.Delete(x.B.Id, m3.CreatedAt.AddMinutes(2))!);
                await db.SaveChangesAsync();
                return 0;
            });
        }

        var caseId = await FileAsync(x.A, x.ExchangeId, new[] { deleted });
        return (x.A, x.B, x.ExchangeId, caseId, plain, edited, deleted);
    }

    [Fact]
    public async Task The_moderator_sees_deleted_messages_and_the_original_versions_of_edits()
    {
        var x = await ChatCaseAsync();
        var moderator = await ModeratorAsync();
        await TakeAsync(moderator, x.CaseId);

        var response = await ChatAsync(moderator.Token, x.CaseId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await JsonAsync(response);
        Assert.Equal(x.ExchangeId, page.GetProperty("exchangeId").GetGuid());
        var items = page.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());

        Assert.Equal("Обычное сообщение", items[x.Plain].GetProperty("text").GetString());
        Assert.Equal(0, items[x.Plain].GetProperty("revisions").GetArrayLength());
        Assert.False(items[x.Plain].GetProperty("isEvidence").GetBoolean());

        Assert.Equal("Исправленный вежливый текст", items[x.Edited].GetProperty("text").GetString());
        var revision = Assert.Single(items[x.Edited].GetProperty("revisions").EnumerateArray());
        Assert.Equal("Первоначальная грубость", revision.GetProperty("text").GetString());

        var deletedItem = items[x.Deleted];
        Assert.NotEqual(JsonValueKind.Null, deletedItem.GetProperty("deletedAt").ValueKind);
        Assert.Equal("Угроза, которую потом стёрли", deletedItem.GetProperty("text").GetString());   // участникам текст стёртого не виден, модератору виден
        Assert.True(deletedItem.GetProperty("isEvidence").GetBoolean());

        // Обычному участнику тот же удалённый текст не показывается.
        var own = await JsonAsync(await SendAsync(x.A.Token, HttpMethod.Get, $"/exchanges/{x.ExchangeId}/messages"));
        var ownDeleted = own.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == x.Deleted);
        Assert.Equal(JsonValueKind.Null, ownDeleted.GetProperty("text").ValueKind);
    }

    [Fact]
    public async Task Every_history_request_is_an_audit_row_and_reading_creates_no_notifications()
    {
        var x = await ChatCaseAsync();
        var moderator = await ModeratorAsync();
        await TakeAsync(moderator, x.CaseId);
        var beforeA = await NotificationCountAsync(x.A);
        var beforeB = await NotificationCountAsync(x.B);

        var first = await ChatAsync(moderator.Token, x.CaseId, "?limit=2");
        var next = (await JsonAsync(first)).GetProperty("nextBefore").GetString();
        await ChatAsync(moderator.Token, x.CaseId, $"?limit=2&before={next}");
        await ChatAsync(moderator.Token, x.CaseId);

        var rows = await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.ActorId == moderator.Id && a.Action == AuditActions.ChatRead).ToListAsync());
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("Exchange", r.TargetType);
            Assert.Equal(x.ExchangeId, r.TargetId);
            Assert.Contains(x.CaseId.ToString(), r.DetailsJson);   // какое обращение
            Assert.InRange((r.CreatedAt - Now).TotalMinutes, -5, 5);   // когда: по часам приложения (общие часы тестов могут быть сдвинуты)
        });
        Assert.Equal(beforeA, await NotificationCountAsync(x.A));
        Assert.Equal(beforeB, await NotificationCountAsync(x.B));
        Assert.Single(await NotificationsAsync(x.B, "CaseTaken"));
    }

    [Fact]
    public async Task A_rejected_history_request_is_not_audited_as_a_read_and_a_bad_limit_is_a_clear_error()
    {
        var x = await ChatCaseAsync();
        var moderator = await ModeratorAsync();
        var stranger = await ModeratorAsync();
        await TakeAsync(moderator, x.CaseId);

        Assert.Equal(HttpStatusCode.Forbidden, (await ChatAsync(stranger.Token, x.CaseId)).StatusCode);
        var bad = await ChatAsync(moderator.Token, x.CaseId, "?limit=500");

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_limit", await CodeAsync(bad));
        Assert.Equal(0, await AuditCountAsync(stranger.Id, AuditActions.ChatRead));
        Assert.Equal(0, await AuditCountAsync(moderator.Id, AuditActions.ChatRead));
    }

    [Fact]
    public async Task Chat_access_is_read_only()
    {
        var x = await ChatCaseAsync();
        var moderator = await ModeratorAsync();
        await TakeAsync(moderator, x.CaseId);

        // Писать, читать как участник и отмечать прочитанным модератор не может: обычный чат закрыт для служебных аккаунтов.
        var write = await SendAsync(moderator.Token, HttpMethod.Post, $"/exchanges/{x.ExchangeId}/messages", new { text = "Привет" });
        var asParticipant = await SendAsync(moderator.Token, HttpMethod.Get, $"/exchanges/{x.ExchangeId}/messages");
        var markRead = await SendAsync(moderator.Token, HttpMethod.Post, $"/exchanges/{x.ExchangeId}/messages/read", new { });
        var viaModerationRoute = await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/chat", new { text = "Привет" });

        Assert.False(write.IsSuccessStatusCode);   // писать в чат можно только через хаб участников: у REST-маршрута для записи метода нет
        Assert.Equal(HttpStatusCode.Forbidden, asParticipant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, markRead.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, viaModerationRoute.StatusCode);
        Assert.Equal(0, await _factory.QueryAsync(db => db.Messages.CountAsync(m => m.ExchangeId == x.ExchangeId && m.SenderId == moderator.Id)));
    }

    // ---------- IDOR ----------

    [Fact]
    public async Task Idor_another_moderator_without_a_grant_gets_403()
    {
        var x = await ChatCaseAsync();
        var owner = await ModeratorAsync();
        var stranger = await ModeratorAsync();
        await TakeAsync(owner, x.CaseId);

        var response = await ChatAsync(stranger.Token, x.CaseId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("chat_access_denied", await CodeAsync(response));
    }

    [Fact]
    public async Task Idor_a_moderator_who_has_not_taken_the_case_has_no_access_to_its_chat()
    {
        var x = await ChatCaseAsync();
        var moderator = await ModeratorAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await ChatAsync(moderator.Token, x.CaseId)).StatusCode);
    }

    [Fact]
    public async Task Idor_a_grant_for_one_exchange_does_not_open_the_chat_of_another()
    {
        var mine = await ChatCaseAsync();
        var foreign = await ChatCaseAsync();
        var moderator = await ModeratorAsync();
        await TakeAsync(moderator, mine.CaseId);

        var response = await ChatAsync(moderator.Token, foreign.CaseId);   // чужое обращение, чужой обмен: доступа нет

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.TargetId == foreign.ExchangeId && a.Action == AuditActions.ChatRead)));
        Assert.Equal(HttpStatusCode.OK, (await ChatAsync(moderator.Token, mine.CaseId)).StatusCode);
    }

    [Fact]
    public async Task Idor_a_grant_is_tied_to_its_case_and_not_to_the_exchange_alone()
    {
        var x = await ChatCaseAsync();
        var moderator = await ModeratorAsync();
        await TakeAsync(moderator, x.CaseId);
        var secondCase = await FileAsync(x.B, x.ExchangeId);   // второе обращение по тому же обмену: его модератор не брал

        Assert.Equal(HttpStatusCode.Forbidden, (await ChatAsync(moderator.Token, secondCase)).StatusCode);
    }

    [Fact]
    public async Task Idor_an_unknown_case_is_404_and_participants_and_admins_get_403()
    {
        var x = await ChatCaseAsync();
        var moderator = await ModeratorAsync();
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var superAdmin = await ModerationTestData.StaffAsync(_factory, UserRole.SuperAdmin);
        await TakeAsync(moderator, x.CaseId);

        Assert.Equal(HttpStatusCode.NotFound, (await ChatAsync(moderator.Token, Guid.NewGuid())).StatusCode);
        foreach (var token in new[] { x.A.Token, x.B.Token, admin.Token, superAdmin.Token })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await ChatAsync(token, x.CaseId)).StatusCode);
        }
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.SuperAdmin)]
    public async Task An_admin_never_reads_a_chat_even_if_a_grant_is_somehow_recorded_for_them(UserRole role)
    {
        var x = await ChatCaseAsync();
        var staff = await ModerationTestData.StaffAsync(_factory, role);
        await _factory.QueryAsync(async db =>
        {
            db.ChatAccessGrants.Add(ChatAccessGrant.Create(x.CaseId, staff.Id, x.ExchangeId, GrantReason.Case, Now));
            await db.SaveChangesAsync();
            return 0;
        });

        using var scope = _factory.Services.CreateScope();
        var policy = scope.ServiceProvider.GetRequiredService<ChatAccessPolicy>();
        Assert.False(await policy.CanReadAsync(staff.Id, role, x.ExchangeId, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, (await ChatAsync(staff.Token, x.CaseId)).StatusCode);
    }

    // ---------- отзыв доступа ----------

    [Fact]
    public async Task Revoking_access_at_a_decision_closes_the_chat_for_the_moderator_with_403()
    {
        var x = await ChatCaseAsync();
        var moderator = await ModeratorAsync();
        await TakeAsync(moderator, x.CaseId);
        Assert.Equal(HttpStatusCode.OK, (await ChatAsync(moderator.Token, x.CaseId)).StatusCode);

        // Решение (следующие шаги) вызывает именно эту операцию в своей транзакции.
        int revoked;
        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ModerationService>();
            var grants = scope.ServiceProvider.GetRequiredService<IChatAccessGrantStore>();
            revoked = await service.RevokeChatAccessAsync(x.CaseId, moderator.Id, CancellationToken.None);
            await grants.SaveAsync(CancellationToken.None);
        }

        Assert.Equal(1, revoked);
        var response = await ChatAsync(moderator.Token, x.CaseId);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("chat_access_denied", await CodeAsync(response));
        Assert.NotNull((await GrantsAsync(x.CaseId)).Single().RevokedAt);
        Assert.Equal(1, await AuditCountAsync(moderator.Id, AuditActions.ChatAccessRevoked, x.ExchangeId));

        var card = await JsonAsync(await SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{x.CaseId}"));
        Assert.False(card.GetProperty("hasChatAccess").GetBoolean());

        using var again = _factory.Services.CreateScope();   // повторный отзыв безвреден
        Assert.Equal(0, await again.ServiceProvider.GetRequiredService<ModerationService>().RevokeChatAccessAsync(x.CaseId, moderator.Id, CancellationToken.None));
    }
}
