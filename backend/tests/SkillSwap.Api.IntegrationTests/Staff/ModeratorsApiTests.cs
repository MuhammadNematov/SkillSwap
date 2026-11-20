using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.Hubs;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Staff;

/// <summary>Модераторы в панели админа: список с нагрузкой, карточка и снятие (возврат обращений в очередь, апелляций админу, отзыв доступов и сеансов, снятие привязки).</summary>
[Collection(ApiCollection.Name)]
public class ModeratorsApiTests
{
    private const string Complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";
    private const string Reason = "Модератор больше не работает в команде SkillSwap.";
    private const string AppealReason = "Не согласен с решением: переписка вырвана из контекста, прошу перепроверить.";
    private const string Conclusion = "Переписку изучил: нарушение подтверждается, решение стоит оставить без изменений.";
    private const string Decision = "Переписка подтверждает жалобу: оскорбления были, договорённости сорваны.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ModeratorsApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

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

    private async Task<List<JsonElement>> NotificationsAsync(string token, string type)
    {
        var items = (await JsonAsync(await SendAsync(token, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return items.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    private Task<int> AuditCountAsync(string action, Guid? target = null, Guid? actor = null) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == action && (target == null || a.TargetId == target) && (actor == null || a.ActorId == actor)));

    private Task<Case> CaseRowAsync(Guid id) => _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task<User> UserAsync(Guid id) => _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<ChatAccessGrant[]> GrantsAsync(Guid caseId) => _factory.QueryAsync(db => db.ChatAccessGrants.AsNoTracking().Where(g => g.CaseId == caseId).ToArrayAsync());

    private Task<StaffActor> AdminAsync(UserRole role = UserRole.Admin) => StaffActor.CreateAsync(_factory, role);

    /// <summary>Модератор с уникальным именем, которое стоит первым в списке (общая база тестов хранит много модераторов).</summary>
    private async Task<StaffPerson> ModeratorAsync(MatchPerson? linked = null)
    {
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator, linked);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == moderator.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Name, "AAA " + Guid.NewGuid().ToString("N")));
            return 0;
        });
        return moderator;
    }

    private sealed record Setup(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid CaseByA, MatchTestWorld World);

    /// <summary>Обмен двух людей и жалоба A на B (ещё не взята).</summary>
    private async Task<Setup> OpenCaseAsync()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var created = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = Complaint });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return new Setup(x.A, x.B, x.ExchangeId, (await JsonAsync(created)).GetProperty("id").GetGuid(), x.World);
    }

    private async Task<Setup> TakenCaseAsync(StaffPerson moderator)
    {
        var s = await OpenCaseAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseByA}/take")).StatusCode);
        return s;
    }

    private Task<HttpResponseMessage> RemoveAsync(StaffActor admin, Guid moderatorId, object? reason = null, object? confirmation = null) =>
        SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff/moderators/{moderatorId}/remove", new { reason = reason ?? Reason, confirmation = confirmation ?? admin.Confirm() });

    private sealed class FakeHubContext : HubCallerContext
    {
        private readonly ClaimsPrincipal _user;

        public FakeHubContext(Guid userId)
        {
            _user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId.ToString())], "Bearer"));
            ConnectionId = Guid.NewGuid().ToString("N");
        }

        public bool Aborted { get; private set; }

        public override string ConnectionId { get; }

        public override string? UserIdentifier => _user.FindFirstValue("sub");

        public override ClaimsPrincipal? User => _user;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override IFeatureCollection Features { get; } = new FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort() => Aborted = true;
    }

    // ======================= список и карточка =======================

    [Fact]
    public async Task Only_admins_and_superadmins_open_the_moderators_panel()
    {
        var (_, personal) = await StaffActor.PersonalAsync(_factory);
        var moderator = await ModeratorAsync();
        var admin = await AdminAsync();
        var superAdmin = await AdminAsync(UserRole.SuperAdmin);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/admin/staff/moderators")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(personal, HttpMethod.Get, "/admin/staff/moderators")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Get, "/admin/staff/moderators")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Get, $"/admin/staff/moderators/{moderator.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Post, $"/admin/staff/moderators/{moderator.Id}/remove", new { reason = Reason })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff/moderators")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(superAdmin.Token, HttpMethod.Get, "/admin/staff/moderators")).StatusCode);
        Assert.Equal("invalid_paging", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff/moderators?pageSize=500")));
    }

    [Fact]
    public async Task The_list_and_the_card_show_the_load_and_the_cases_in_hand()
    {
        var admin = await AdminAsync();
        var moderator = await ModeratorAsync();
        var idle = await ModeratorAsync();
        var s = await TakenCaseAsync(moderator);
        var second = await OpenCaseAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{second.CaseByA}/take")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{second.CaseByA}/questions", new { recipient = "Both", text = "Опишите, пожалуйста, что произошло." })).StatusCode);
        var third = await OpenCaseAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{third.CaseByA}/take")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{third.CaseByA}/resolve", new { decision = "NoViolation", alsoWarned = false, reason = Decision })).StatusCode);

        var list = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff/moderators?pageSize=50"));
        var card = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/staff/moderators/{moderator.Id}"));

        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == moderator.Id);
        var load = item.GetProperty("load");
        Assert.Equal(1, load.GetProperty("inProgress").GetInt32());
        Assert.Equal(1, load.GetProperty("waitingResponse").GetInt32());
        Assert.Equal(0, load.GetProperty("appealReview").GetInt32());
        Assert.Equal(1, load.GetProperty("resolvedLast30Days").GetInt32());
        Assert.False(item.GetProperty("removed").GetBoolean());
        Assert.False(item.GetProperty("personalLinked").GetBoolean());
        var idleItem = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == idle.Id);
        Assert.Equal(0, idleItem.GetProperty("load").GetProperty("inProgress").GetInt32());
        var held = card.GetProperty("heldCases").EnumerateArray().ToList();
        Assert.Equal(2, held.Count);
        Assert.Equal("InProgress", held.Single(h => h.GetProperty("id").GetGuid() == s.CaseByA).GetProperty("status").GetString());
        Assert.Equal("WaitingResponse", held.Single(h => h.GetProperty("id").GetGuid() == second.CaseByA).GetProperty("status").GetString());
        Assert.All(held, h => Assert.Equal("Moderator", h.GetProperty("role").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(admin.Token, HttpMethod.Get, $"/admin/staff/moderators/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(admin.Token, HttpMethod.Get, $"/admin/staff/moderators/{admin.Id}")).StatusCode);   // админа здесь нет
    }

    // ======================= снятие =======================

    [Fact]
    public async Task Removing_a_moderator_returns_the_cases_in_work_to_the_queue_and_revokes_the_chat_access()
    {
        var admin = await AdminAsync();
        var moderator = await ModeratorAsync();
        var s = await TakenCaseAsync(moderator);
        var waiting = await OpenCaseAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{waiting.CaseByA}/take")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{waiting.CaseByA}/questions", new { recipient = "Both", text = "Опишите, пожалуйста, что произошло." })).StatusCode);
        Assert.NotNull((await CaseRowAsync(waiting.CaseByA)).ResponseDeadlineAt);
        var inChat = await SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{s.CaseByA}/chat");
        Assert.Equal(HttpStatusCode.OK, inChat.StatusCode);   // пока доступ есть

        var response = await RemoveAsync(admin, moderator.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await JsonAsync(response);
        Assert.Equal(2, result.GetProperty("casesReturned").GetInt32());
        Assert.Equal(0, result.GetProperty("appealReviewsReturned").GetInt32());
        Assert.False(result.GetProperty("personalUnlinked").GetBoolean());
        Assert.True(result.GetProperty("moderator").GetProperty("moderator").GetProperty("removed").GetBoolean());
        Assert.Empty(result.GetProperty("moderator").GetProperty("heldCases").EnumerateArray());
        foreach (var (id, exchangeId) in new[] { (s.CaseByA, s.ExchangeId), (waiting.CaseByA, waiting.ExchangeId) })
        {
            var row = await CaseRowAsync(id);
            Assert.Equal(CaseStatus.Open, row.Status);
            Assert.Null(row.ModeratorId);
            Assert.Null(row.ResponseDeadlineAt);
            Assert.All(await GrantsAsync(id), g => Assert.NotNull(g.RevokedAt));   // доступ к чату отозван
            Assert.Equal(1, await AuditCountAsync("CaseReleased", id, admin.Id));
            Assert.Equal(1, await AuditCountAsync("ChatAccessRevoked", exchangeId, admin.Id));
        }

        var user = await UserAsync(moderator.Id);
        Assert.Equal(UserStatus.Blocked, user.Status);
        Assert.Equal(1, await AuditCountAsync("StaffRemoved", moderator.Id, admin.Id));
        var removal = await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "StaffRemoved" && a.TargetId == moderator.Id));
        var details = JsonDocument.Parse(removal.DetailsJson).RootElement;
        Assert.Equal(2, details.GetProperty("casesReturned").GetInt32());
        Assert.Equal(Reason, details.GetProperty("reason").GetString());
        Assert.Equal(1, (await NotificationsAsync(s.A.Token, "CaseReturnedToQueue")).Count(n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == s.CaseByA));
        Assert.Equal(1, (await NotificationsAsync(s.B.Token, "CaseReturnedToQueue")).Count(n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == s.CaseByA));
        Assert.Empty(await NotificationsAsync(moderator.Token, "CaseTakenAway"));   // снятому уведомление о «забрали» не нужно

        // Снятый больше ничего не читает, даже с живым токеном.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{s.CaseByA}/chat")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Get, "/moderation/cases")).StatusCode);

        // Другой модератор берёт обращение из очереди заново и получает свой доступ.
        var next = await ModeratorAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(next.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseByA}/take")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(next.Token, HttpMethod.Get, $"/moderation/cases/{s.CaseByA}/chat")).StatusCode);
        Assert.Equal(2, (await NotificationsAsync(s.A.Token, "CaseTaken")).Count(n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == s.CaseByA));   // прежнее взятие и новое
    }

    [Fact]
    public async Task Removing_a_moderator_revokes_the_sessions_and_drops_the_realtime_connections()
    {
        var admin = await AdminAsync();
        var moderator = await ModeratorAsync();
        var other = await ModeratorAsync();
        await _factory.QueryAsync(async db =>
        {
            var now = _factory.Clock.GetUtcNow().UtcDateTime;
            db.Set<RefreshToken>().Add(RefreshToken.Create(moderator.Id, Guid.NewGuid().ToString("N"), now.AddDays(10), "test", null, Guid.NewGuid(), now));
            db.Set<RefreshToken>().Add(RefreshToken.Create(moderator.Id, Guid.NewGuid().ToString("N"), now.AddDays(10), "test", null, Guid.NewGuid(), now));
            await db.SaveChangesAsync();
            return 0;
        });
        var registry = _factory.Services.GetRequiredService<ConnectionRegistry>();
        var removed = new FakeHubContext(moderator.Id);
        var colleague = new FakeHubContext(other.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var profiles = scope.ServiceProvider.GetRequiredService<SkillSwap.Application.Profile.IProfileStore>();
            Assert.True(await registry.AcceptAsync(removed, profiles));
            Assert.True(await registry.AcceptAsync(colleague, profiles));
        }

        Assert.Equal(HttpStatusCode.OK, (await RemoveAsync(admin, moderator.Id)).StatusCode);

        var tokens = await _factory.QueryAsync(db => db.Set<RefreshToken>().AsNoTracking().Where(t => t.UserId == moderator.Id).ToListAsync());
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        Assert.True(removed.Aborted);
        Assert.False(colleague.Aborted);
        var late = new FakeHubContext(moderator.Id);
        using var again = _factory.Services.CreateScope();
        Assert.False(await registry.AcceptAsync(late, again.ServiceProvider.GetRequiredService<SkillSwap.Application.Profile.IProfileStore>()));
    }

    private async Task<(Setup S, StaffPerson Author, StaffPerson Reviewer)> UnderAppealReviewAsync(StaffActor admin)
    {
        var author = await ModeratorAsync();
        var reviewer = await ModeratorAsync();
        var s = await TakenCaseAsync(author);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(author.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseByA}/resolve", new { decision = "Warning", alsoWarned = false, reason = Decision })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s.B.Token, HttpMethod.Post, $"/cases/{s.CaseByA}/appeal", new { reason = AppealReason })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/cases/{s.CaseByA}/appeal/reviewer", new { moderatorId = reviewer.Id })).StatusCode);
        Assert.Equal(CaseStatus.AppealReview, (await CaseRowAsync(s.CaseByA)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(reviewer.Token, HttpMethod.Get, $"/moderation/cases/{s.CaseByA}/chat")).StatusCode);
        return (s, author, reviewer);
    }

    [Fact]
    public async Task Removing_the_reviewer_returns_the_appeal_to_the_admins_who_can_assign_another_one()
    {
        var admin = await AdminAsync();
        var otherAdmin = await AdminAsync();
        var (s, author, reviewer) = await UnderAppealReviewAsync(admin);
        var card = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/staff/moderators/{reviewer.Id}"));
        Assert.Equal("AppealReviewer", Assert.Single(card.GetProperty("heldCases").EnumerateArray()).GetProperty("role").GetString());
        Assert.Equal(1, card.GetProperty("moderator").GetProperty("load").GetProperty("appealReview").GetInt32());

        var response = await RemoveAsync(admin, reviewer.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await JsonAsync(response);
        Assert.Equal(0, result.GetProperty("casesReturned").GetInt32());
        Assert.Equal(1, result.GetProperty("appealReviewsReturned").GetInt32());
        var row = await CaseRowAsync(s.CaseByA);
        Assert.Equal(CaseStatus.Appealed, row.Status);                 // ждёт админа
        Assert.Equal(author.Id, row.ModeratorId);                      // решение автора не трогаем
        var appeal = await _factory.QueryAsync(db => db.CaseAppeals.AsNoTracking().SingleAsync(a => a.CaseId == s.CaseByA));
        Assert.Null(appeal.ReviewerModeratorId);
        Assert.All(await GrantsAsync(s.CaseByA), g => Assert.NotNull(g.RevokedAt));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(reviewer.Token, HttpMethod.Get, $"/moderation/cases/{s.CaseByA}/chat")).StatusCode);
        Assert.Equal(1, await AuditCountAsync("AppealReviewReleased", s.CaseByA, admin.Id));
        var note = Assert.Single(await NotificationsAsync(otherAdmin.Token, "AppealReviewerReleased"), n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == s.CaseByA).GetProperty("payload");
        Assert.Equal("staff_removed", note.GetProperty("reason").GetString());
        Assert.DoesNotContain(await NotificationsAsync(admin.Token, "AppealReviewerReleased"), n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == s.CaseByA);   // сам снимавший не уведомляется

        var another = await ModeratorAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/cases/{s.CaseByA}/appeal/reviewer", new { moderatorId = another.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(another.Token, HttpMethod.Get, $"/moderation/cases/{s.CaseByA}/chat")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/cases/{s.CaseByA}/appeal/reviewer", new { moderatorId = reviewer.Id })).StatusCode);   // снятого назначить нельзя
    }

    [Fact]
    public async Task Removing_the_author_of_a_decision_leaves_the_finished_case_and_its_appeal_alone()
    {
        var admin = await AdminAsync();
        var (s, author, reviewer) = await UnderAppealReviewAsync(admin);

        var response = await RemoveAsync(admin, author.Id);

        var result = await JsonAsync(response);
        Assert.Equal(0, result.GetProperty("casesReturned").GetInt32());
        Assert.Equal(0, result.GetProperty("appealReviewsReturned").GetInt32());
        var row = await CaseRowAsync(s.CaseByA);
        Assert.Equal(CaseStatus.AppealReview, row.Status);
        Assert.Equal(author.Id, row.ModeratorId);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(reviewer.Token, HttpMethod.Get, $"/moderation/cases/{s.CaseByA}/chat")).StatusCode);   // проверяющий продолжает
    }

    [Fact]
    public async Task Removing_a_moderator_unlinks_the_personal_account_but_keeps_the_conflict_of_interest()
    {
        var admin = await AdminAsync();
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var moderator = await ModeratorAsync(linked: x.A);
        var byA = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = Complaint });
        var caseId = (await JsonAsync(byA)).GetProperty("id").GetGuid();

        var response = await RemoveAsync(admin, moderator.Id);

        Assert.True((await JsonAsync(response)).GetProperty("personalUnlinked").GetBoolean());
        Assert.Null((await UserAsync(moderator.Id)).LinkedUserId);
        var history = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/staff/{moderator.Id}/personal-links"));
        var link = Assert.Single(history.EnumerateArray());
        Assert.False(link.GetProperty("isActive").GetBoolean());
        Assert.Equal(admin.Id, link.GetProperty("unlinkedById").GetGuid());
        Assert.Equal(1, await AuditCountAsync("PersonalAccountUnlinked", moderator.Id, admin.Id));
        Assert.Equal(caseId, (await CaseRowAsync(caseId)).Id);
    }

    [Fact]
    public async Task A_removal_is_refused_for_oneself_for_admins_for_unknown_ids_and_without_a_proper_confirmation()
    {
        var admin = await AdminAsync();
        var otherAdmin = await AdminAsync();
        var superAdmin = await AdminAsync(UserRole.SuperAdmin);
        var moderator = await ModeratorAsync();
        var (regular, _) = await StaffActor.PersonalAsync(_factory);

        var self = await RemoveAsync(admin, admin.Id);
        var peer = await RemoveAsync(admin, otherAdmin.Id);
        var bySuper = await RemoveAsync(superAdmin, otherAdmin.Id);
        var unknown = await RemoveAsync(admin, Guid.NewGuid());
        var asRegular = await RemoveAsync(admin, regular.Id);
        var tooShort = await RemoveAsync(admin, moderator.Id, reason: "мало");
        var noConfirmation = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff/moderators/{moderator.Id}/remove", new { reason = Reason });
        var wrongPassword = await RemoveAsync(admin, moderator.Id, confirmation: new { password = "Wrong-password-1", code = admin.Recovery[admin.Next++], isRecoveryCode = true });

        Assert.Equal("cannot_change_own_rights", await CodeAsync(self));
        Assert.Equal("staff_role_not_allowed", await CodeAsync(peer));
        Assert.Equal(HttpStatusCode.Conflict, bySuper.StatusCode);
        Assert.Equal("not_a_moderator", await CodeAsync(bySuper));
        Assert.Equal("moderator_not_found", await CodeAsync(unknown));
        Assert.Equal("moderator_not_found", await CodeAsync(asRegular));
        Assert.Equal("removal_reason_too_short", await CodeAsync(tooShort));
        Assert.Equal("step_up_required", await CodeAsync(noConfirmation));
        Assert.Equal("invalid_password", await CodeAsync(wrongPassword));
        Assert.Equal(UserStatus.Active, (await UserAsync(moderator.Id)).Status);
        Assert.Equal(0, await AuditCountAsync("StaffRemoved", moderator.Id));

        Assert.Equal(HttpStatusCode.OK, (await RemoveAsync(admin, moderator.Id)).StatusCode);
        var again = await RemoveAsync(admin, moderator.Id);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("staff_already_removed", await CodeAsync(again));
        Assert.Equal(1, await AuditCountAsync("StaffRemoved", moderator.Id));
    }

    [Fact]
    public async Task The_removed_moderators_appear_only_in_the_removed_list()
    {
        var admin = await AdminAsync();
        var moderator = await ModeratorAsync();
        Assert.Equal(HttpStatusCode.OK, (await RemoveAsync(admin, moderator.Id)).StatusCode);

        var active = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff/moderators?pageSize=50"));
        var removed = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff/moderators?removed=true&pageSize=50"));

        Assert.DoesNotContain(active.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == moderator.Id);
        Assert.True(removed.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == moderator.Id).GetProperty("removed").GetBoolean());
    }
}
