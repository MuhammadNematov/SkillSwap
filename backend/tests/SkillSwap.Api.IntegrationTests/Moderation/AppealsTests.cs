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
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>
/// Досрочное завершение ожидания пояснений, апелляция (подача, материалы для админа, повторная проверка другим модератором, заключение, окончательное решение
/// с откатом последствий) и запросы на блокировку (раздел 8.2 и 8.3 ТЗ) на настоящем приложении и БД.
/// </summary>
[Collection(ApiCollection.Name)]
public class AppealsTests
{
    private const string Complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";
    private const string Reason = "Переписка подтверждает жалобу: оскорбления были, договорённости сорваны.";
    private const string GoodText = "Очень понятно объясняет, уроки были полезными и по делу.";
    private const string AppealReason = "Не согласен с решением: переписка вырвана из контекста, прошу перепроверить.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public AppealsTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private InlineBackgroundJobs Jobs => (InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();

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

    private sealed record Scenario(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid CaseId, StaffPerson Moderator, MatchTestWorld World);

    private async Task<Scenario> TakenAsync(string type = "Conduct", string category = "Insults")
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var created = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type, category, exchangeId = x.ExchangeId, description = Complaint });
        var caseId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/take")).StatusCode);
        return new Scenario(x.A, x.B, x.ExchangeId, caseId, moderator, x.World);
    }

    private Task<HttpResponseMessage> ResolveAsync(Scenario s, string decision, bool alsoWarned = false) =>
        SendAsync(s.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/resolve", new { decision, alsoWarned, reason = Reason });

    private async Task<Scenario> ResolvedAsync(string decision = "Warning", bool alsoWarned = false)
    {
        var s = await TakenAsync();
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(s, decision, alsoWarned)).StatusCode);
        return s;
    }

    private Task<HttpResponseMessage> AppealAsync(MatchPerson who, Guid caseId, string? reason = AppealReason) =>
        SendAsync(who.Token, HttpMethod.Post, $"/cases/{caseId}/appeal", new { reason });

    private async Task<Scenario> AppealedAsync(string decision = "Warning", bool alsoWarned = false)
    {
        var s = await ResolvedAsync(decision, alsoWarned);
        Assert.Equal(HttpStatusCode.OK, (await AppealAsync(s.B, s.CaseId)).StatusCode);
        return s;
    }

    private Task<HttpResponseMessage> DecideAsync(StaffPerson admin, Guid caseId, string outcome, string? newDecision = null, bool alsoWarned = false, string? reason = Reason) =>
        SendAsync(admin.Token, HttpMethod.Post, $"/admin/cases/{caseId}/appeal/decision", new { outcome, newDecision, alsoWarned, reason });

    private Task<HttpResponseMessage> AssignAsync(StaffPerson admin, Guid caseId, Guid moderatorId) =>
        SendAsync(admin.Token, HttpMethod.Post, $"/admin/cases/{caseId}/appeal/reviewer", new { moderatorId });

    private Task<HttpResponseMessage> ChatAsync(StaffPerson moderator, Guid caseId) => SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{caseId}/chat");

    private Task<StaffPerson> AdminAsync() => ModerationTestData.StaffAsync(_factory, UserRole.Admin);

    private Task<StaffPerson> ModeratorAsync(MatchPerson? linked = null) => ModerationTestData.StaffAsync(_factory, UserRole.Moderator, linked);

    private async Task<List<JsonElement>> NotificationsAsync(string token, string type)
    {
        var items = (await JsonAsync(await SendAsync(token, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return items.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    private Task<List<JsonElement>> NotificationsAsync(MatchPerson who, string type) => NotificationsAsync(who.Token, type);

    private Task<Case> CaseAsync(Guid id) => _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task<CaseAppeal> AppealRowAsync(Guid caseId) => _factory.QueryAsync(db => db.CaseAppeals.AsNoTracking().SingleAsync(a => a.CaseId == caseId));

    private Task<Exchange> ExchangeRowAsync(Guid id) => _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == id));

    private Task<User> UserAsync(Guid id) => _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<Warning[]> WarningsAsync(Guid userId) => _factory.QueryAsync(db => db.Warnings.AsNoTracking().Where(w => w.UserId == userId).ToArrayAsync());

    private Task<BanRequest[]> BansAsync(Guid userId) => _factory.QueryAsync(db => db.BanRequests.AsNoTracking().Where(b => b.UserId == userId).ToArrayAsync());

    private Task<int> AuditCountAsync(string action, Guid? target = null, Guid? actor = null) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == action && (target == null || a.TargetId == target) && (actor == null || a.ActorId == actor)));

    private Task<ChatAccessGrant[]> GrantsAsync(Guid caseId) => _factory.QueryAsync(db => db.ChatAccessGrants.AsNoTracking().Where(g => g.CaseId == caseId).ToArrayAsync());

    // ======================= досрочное завершение ожидания =======================

    private Task AskBothAsync(Scenario s) =>
        SendAsync(s.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/questions", new { recipient = "Both", text = "Опишите, пожалуйста, что произошло." });

    [Fact]
    public async Task The_moderator_stops_waiting_with_a_reason_and_can_then_decide()
    {
        var s = await TakenAsync();
        await AskBothAsync(s);
        Assert.Equal("waiting_for_explanations", await CodeAsync(await ResolveAsync(s, "NoViolation")));

        var response = await SendAsync(s.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/stop-waiting", new { reason = "Стороны не отвечают, материалов достаточно." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await CaseAsync(s.CaseId);
        Assert.Equal(CaseStatus.InProgress, stored.Status);
        Assert.Null(stored.ResponseDeadlineAt);
        var audit = await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "ExplanationStopped" && a.TargetId == s.CaseId));
        Assert.Contains("материалов достаточно", audit.DetailsJson);
        Assert.Equal(s.Moderator.Id, audit.ActorId);

        // Поздний ответ не принимается, решение выносится.
        var question = (await JsonAsync(await SendAsync(s.A.Token, HttpMethod.Get, $"/cases/{s.CaseId}/explanations"))).GetProperty("items")[0].GetProperty("id").GetGuid();
        var late = await SendAsync(s.A.Token, HttpMethod.Post, $"/cases/{s.CaseId}/explanations", new { questionId = question, text = "Отвечаю с опозданием." });
        Assert.Equal("explanation_not_requested", await CodeAsync(late));
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(s, "NoViolation")).StatusCode);
    }

    [Fact]
    public async Task Stopping_needs_a_reason_the_owner_and_a_waiting_case()
    {
        var s = await TakenAsync();
        var other = await ModeratorAsync();
        var admin = await AdminAsync();
        var body = new { reason = "Стороны не отвечают, материалов достаточно." };
        var idle = await SendAsync(s.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/stop-waiting", body);
        Assert.Equal(HttpStatusCode.Conflict, idle.StatusCode);   // ничего не ждём
        Assert.Equal("invalid_state_transition", await CodeAsync(idle));

        await AskBothAsync(s);
        var shortReason = await SendAsync(s.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/stop-waiting", new { reason = "мало" });
        var noReason = await SendAsync(s.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/stop-waiting", new { reason = (string?)null });

        Assert.Equal("stop_waiting_reason_too_short", await CodeAsync(shortReason));
        Assert.Equal("stop_waiting_reason_too_short", await CodeAsync(noReason));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/stop-waiting", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(admin.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/stop-waiting", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(s.A.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/stop-waiting", body)).StatusCode);
        Assert.Equal(CaseStatus.WaitingResponse, (await CaseAsync(s.CaseId)).Status);
        Assert.Equal(0, await AuditCountAsync("ExplanationStopped", s.CaseId));
    }

    [Fact]
    public async Task The_exchange_can_be_frozen_and_unfrozen_while_waiting_for_explanations()
    {
        var s = await TakenAsync();
        await AskBothAsync(s);
        Assert.Equal(CaseStatus.WaitingResponse, (await CaseAsync(s.CaseId)).Status);

        var frozen = await SendAsync(s.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/freeze-exchange");
        Assert.Equal(HttpStatusCode.OK, frozen.StatusCode);
        Assert.Equal(ExchangeStatus.Frozen, (await ExchangeRowAsync(s.ExchangeId)).Status);
        Assert.Equal(CaseStatus.WaitingResponse, (await CaseAsync(s.CaseId)).Status);   // ожидание не сбилось

        var released = await SendAsync(s.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/unfreeze-exchange");
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Equal(ExchangeStatus.Active, (await ExchangeRowAsync(s.ExchangeId)).Status);
    }

    // ======================= подача апелляции =======================

    [Fact]
    public async Task The_accused_files_an_appeal_and_the_other_side_and_admins_are_told()
    {
        var admin = await AdminAsync();
        var s = await ResolvedAsync();
        var before = await CaseAsync(s.CaseId);
        Assert.Equal(CaseStatus.Resolved, before.Status);
        var open = await JsonAsync(await SendAsync(s.B.Token, HttpMethod.Get, $"/cases/{s.CaseId}"));
        Assert.True(open.GetProperty("canAppeal").GetBoolean());

        var response = await AppealAsync(s.B, s.CaseId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("Appealed", dto.GetProperty("status").GetString());
        Assert.False(dto.GetProperty("canAppeal").GetBoolean());
        Assert.Equal(AppealReason, dto.GetProperty("appeal").GetProperty("reason").GetString());
        Assert.False(dto.GetProperty("appeal").GetProperty("reviewInProgress").GetBoolean());
        var stored = await CaseAsync(s.CaseId);
        Assert.Equal(CaseStatus.Appealed, stored.Status);
        Assert.True(stored.WasAppealed);
        Assert.Equal(s.B.Id, (await AppealRowAsync(s.CaseId)).AppellantId);
        Assert.Equal(1, await AuditCountAsync("CaseAppealed", s.CaseId, s.B.Id));
        Assert.Single(await NotificationsAsync(s.A, "CaseAppealed"));
        Assert.Empty(await NotificationsAsync(s.B, "CaseAppealed"));
        Assert.Single(await NotificationsAsync(admin.Token, "CaseAppealed"), n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == s.CaseId);

        var forA = await JsonAsync(await SendAsync(s.A.Token, HttpMethod.Get, $"/cases/{s.CaseId}"));
        Assert.Equal("Appealed", forA.GetProperty("status").GetString());   // вторая сторона видит, что решение обжалуют
    }

    [Fact]
    public async Task Either_side_can_appeal_but_only_once()
    {
        var s = await ResolvedAsync();

        Assert.Equal(HttpStatusCode.OK, (await AppealAsync(s.A, s.CaseId)).StatusCode);
        var again = await AppealAsync(s.B, s.CaseId);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(again));
        Assert.Equal(s.A.Id, (await AppealRowAsync(s.CaseId)).AppellantId);
    }

    [Fact]
    public async Task An_appeal_needs_a_decision_a_party_a_reason_and_a_regular_account()
    {
        var running = await TakenAsync();
        var s = await ResolvedAsync();
        var stranger = await s.World.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));

        var early = await AppealAsync(running.A, running.CaseId);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AppealAsync(stranger, s.CaseId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(s.Moderator.Token, HttpMethod.Post, $"/cases/{s.CaseId}/appeal", new { reason = AppealReason })).StatusCode);
        var shortReason = await AppealAsync(s.B, s.CaseId, "мало");
        Assert.Equal("appeal_reason_too_short", await CodeAsync(shortReason));
        Assert.Equal(CaseStatus.Resolved, (await CaseAsync(s.CaseId)).Status);
    }

    [Fact]
    public async Task An_appeal_after_seven_days_is_refused_and_the_flag_goes_down()
    {
        var s = await ResolvedAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == s.CaseId).ExecuteUpdateAsync(x => x.SetProperty(c => c.ResolvedAt, Now.AddDays(-8)));
            return 0;
        });

        var dto = await JsonAsync(await SendAsync(s.B.Token, HttpMethod.Get, $"/cases/{s.CaseId}"));
        var response = await AppealAsync(s.B, s.CaseId);

        Assert.False(dto.GetProperty("canAppeal").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("appeal_window_closed", await CodeAsync(response));
    }

    [Fact]
    public async Task An_appealed_case_is_not_closed_by_the_seven_day_job()
    {
        var s = await AppealedAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == s.CaseId).ExecuteUpdateAsync(x => x.SetProperty(c => c.ResolvedAt, Now.AddDays(-9)));
            return 0;
        });
        using var scope = _factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<CaseJobs>().CloseIfDueAsync(s.CaseId);

        Assert.Equal(CaseStatus.Appealed, (await CaseAsync(s.CaseId)).Status);
    }

    // ======================= материалы для админа =======================

    [Fact]
    public async Task The_admin_sees_the_case_materials_and_the_evidence_but_not_the_whole_chat()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var moderator = await ModeratorAsync();
        var admin = await AdminAsync();
        var plain = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Обычное сообщение, не доказательство");
        var evidence = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Грубость, приложенная как доказательство");
        var secret = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.A.Id, "Личная переписка, которую админ видеть не должен");
        var created = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = Complaint, messageIds = new[] { evidence } });
        var caseId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/take");
        await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/resolve", new { decision = "Warning", alsoWarned = false, reason = Reason });

        var response = await SendAsync(admin.Token, HttpMethod.Get, $"/admin/cases/{caseId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var detail = JsonDocument.Parse(raw).RootElement;
        var items = detail.GetProperty("evidence").EnumerateArray().ToList();
        Assert.Equal(evidence, Assert.Single(items).GetProperty("id").GetGuid());
        Assert.Contains("Грубость, приложенная как доказательство", raw);
        Assert.DoesNotContain("Обычное сообщение", raw);   // остальной чат в материалы не попадает
        Assert.DoesNotContain("Личная переписка", raw);
        Assert.Equal("Warning", detail.GetProperty("case").GetProperty("decision").GetString());
        Assert.Equal(1, detail.GetProperty("warnings").GetArrayLength());
        Assert.Equal(1, await AuditCountAsync("CaseMaterialsRead", caseId, admin.Id));
        Assert.NotEqual(plain, secret);

        // Полная переписка админу закрыта.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(admin.Token, HttpMethod.Get, $"/moderation/cases/{caseId}/chat")).StatusCode);
    }

    [Fact]
    public async Task Case_materials_and_lists_are_for_admins_only_and_withdrawn_cases_are_hidden()
    {
        var s = await ResolvedAsync();
        var admin = await AdminAsync();
        var superAdmin = await ModerationTestData.StaffAsync(_factory, UserRole.SuperAdmin);
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var created = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = Complaint });
        var withdrawn = (await JsonAsync(created)).GetProperty("id").GetGuid();
        await SendAsync(x.A.Token, HttpMethod.Post, $"/cases/{withdrawn}/withdraw");

        foreach (var token in new[] { s.Moderator.Token, s.A.Token })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Get, "/admin/cases")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Get, $"/admin/cases/{s.CaseId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Get, "/admin/ban-requests")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Get, "/admin/moderators")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/admin/cases")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(superAdmin.Token, HttpMethod.Get, $"/admin/cases/{s.CaseId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(admin.Token, HttpMethod.Get, $"/admin/cases/{withdrawn}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(admin.Token, HttpMethod.Get, $"/admin/cases/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal("invalid_paging", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/cases?page=0")));
    }

    [Fact]
    public async Task The_admin_list_filters_by_status_and_lists_moderators_without_staff_of_other_roles()
    {
        var s = await AppealedAsync();
        var admin = await AdminAsync();

        var appealed = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/cases?status=Appealed&pageSize=50"));
        var moderators = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/moderators"));

        var items = appealed.GetProperty("items").EnumerateArray().ToList();
        Assert.All(items, i => Assert.Equal("Appealed", i.GetProperty("status").GetString()));
        Assert.Contains(items, i => i.GetProperty("id").GetGuid() == s.CaseId);
        var ids = moderators.EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(s.Moderator.Id, ids);
        Assert.DoesNotContain(admin.Id, ids);
    }

    // ======================= повторная проверка =======================

    [Fact]
    public async Task Assigning_another_moderator_gives_them_chat_access_and_tells_both_sides_exactly_once()
    {
        var s = await AppealedAsync();
        await ModerationTestData.MessageAsync(_factory, s.ExchangeId, s.B.Id, "Сообщение для повторной проверки");
        var admin = await AdminAsync();
        var reviewer = await ModeratorAsync();

        var response = await AssignAsync(admin, s.CaseId, reviewer.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AppealReview", (await JsonAsync(response)).GetProperty("status").GetString());
        var grants = await GrantsAsync(s.CaseId);
        var grant = Assert.Single(grants, g => g.ModeratorId == reviewer.Id);
        Assert.Equal(GrantReason.AppealReview, grant.Reason);
        Assert.Equal(s.ExchangeId, grant.ExchangeId);
        Assert.Null(grant.RevokedAt);
        Assert.Equal(1, await AuditCountAsync("AppealReviewAssigned", s.CaseId, admin.Id));
        Assert.Equal(1, await AuditCountAsync("ChatAccessGranted", s.ExchangeId, admin.Id));
        var appeal = await AppealRowAsync(s.CaseId);
        Assert.Equal(reviewer.Id, appeal.ReviewerModeratorId);
        Assert.Equal(admin.Id, appeal.AdminId);

        var forA = Assert.Single(await NotificationsAsync(s.A, "CaseAppealReviewAssigned")).GetProperty("payload");
        var forB = Assert.Single(await NotificationsAsync(s.B, "CaseAppealReviewAssigned")).GetProperty("payload");
        Assert.Equal("Opener", forA.GetProperty("role").GetString());
        Assert.Equal("Against", forB.GetProperty("role").GetString());
        Assert.Single(await NotificationsAsync(reviewer.Token, "AppealReviewAssigned"));

        // Проверяющий видит карточку с причиной апелляции и читает чат; первый модератор доступа не имеет.
        var card = await JsonAsync(await SendAsync(reviewer.Token, HttpMethod.Get, $"/moderation/cases/{s.CaseId}"));
        Assert.True(card.GetProperty("isAppealReviewer").GetBoolean());
        Assert.True(card.GetProperty("hasChatAccess").GetBoolean());
        Assert.Equal(AppealReason, card.GetProperty("appeal").GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.OK, (await ChatAsync(reviewer, s.CaseId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ChatAsync(s.Moderator, s.CaseId)).StatusCode);

        var mine = await JsonAsync(await SendAsync(reviewer.Token, HttpMethod.Get, "/moderation/cases?scope=Appeals"));
        Assert.Contains(mine.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == s.CaseId);
        var others = await JsonAsync(await SendAsync(s.Moderator.Token, HttpMethod.Get, "/moderation/cases?scope=Appeals"));
        Assert.DoesNotContain(others.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == s.CaseId);
    }

    [Fact]
    public async Task The_reviewer_cannot_be_the_author_of_the_decision_a_conflicted_moderator_or_not_a_moderator()
    {
        var s = await AppealedAsync();
        var admin = await AdminAsync();
        var linkedToA = await ModeratorAsync(linked: s.A);
        var linkedToFormerPartner = await ModeratorAsync(linked: s.B);

        var author = await AssignAsync(admin, s.CaseId, s.Moderator.Id);
        var conflict = await AssignAsync(admin, s.CaseId, linkedToA.Id);
        var conflictByPartner = await AssignAsync(admin, s.CaseId, linkedToFormerPartner.Id);
        var notModerator = await AssignAsync(admin, s.CaseId, admin.Id);
        var unknown = await AssignAsync(admin, s.CaseId, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, author.StatusCode);
        Assert.Equal("appeal_reviewer_must_differ", await CodeAsync(author));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("conflict_of_interest", await CodeAsync(conflict));
        Assert.Equal("conflict_of_interest", await CodeAsync(conflictByPartner));
        Assert.Equal("not_a_moderator", await CodeAsync(notModerator));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(CaseStatus.Appealed, (await CaseAsync(s.CaseId)).Status);
        Assert.Empty((await GrantsAsync(s.CaseId)).Where(g => g.Reason == GrantReason.AppealReview));
        Assert.Empty(await NotificationsAsync(s.A, "CaseAppealReviewAssigned"));
    }

    [Fact]
    public async Task A_reviewer_is_assigned_only_once_to_an_appealed_case_by_an_admin()
    {
        var s = await AppealedAsync();
        var admin = await AdminAsync();
        var first = await ModeratorAsync();
        var second = await ModeratorAsync();
        var resolvedOnly = await ResolvedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await AssignAsync(second, s.CaseId, first.Id)).StatusCode);   // назначает только админ
        Assert.Equal(HttpStatusCode.NotFound, (await AssignAsync(admin, resolvedOnly.CaseId, first.Id)).StatusCode);   // апелляции нет
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(admin, s.CaseId, first.Id)).StatusCode);

        var again = await AssignAsync(admin, s.CaseId, second.Id);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(again));
        Assert.Single((await GrantsAsync(s.CaseId)).Where(g => g.Reason == GrantReason.AppealReview));
    }

    [Fact]
    public async Task The_admin_cannot_decide_while_the_review_is_in_progress()
    {
        var s = await AppealedAsync();
        var admin = await AdminAsync();
        await AssignAsync(admin, s.CaseId, (await ModeratorAsync()).Id);

        var response = await DecideAsync(admin, s.CaseId, "Upheld");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("appeal_review_in_progress", await CodeAsync(response));
        Assert.Equal(CaseStatus.AppealReview, (await CaseAsync(s.CaseId)).Status);
    }

    // ======================= заключение (сквозной тест) =======================

    [Fact]
    public async Task End_to_end_the_reviewer_reads_the_chat_writes_the_conclusion_and_loses_access_with_403_and_an_audit_record()
    {
        var s = await AppealedAsync();
        await ModerationTestData.MessageAsync(_factory, s.ExchangeId, s.B.Id, "Сообщение, которое читает проверяющий");
        var admin = await AdminAsync();
        var reviewer = await ModeratorAsync();
        await AssignAsync(admin, s.CaseId, reviewer.Id);
        Assert.Equal(HttpStatusCode.OK, (await ChatAsync(reviewer, s.CaseId)).StatusCode);
        Assert.Equal(0, await AuditCountAsync("ChatAccessRevoked", s.ExchangeId, reviewer.Id));

        var response = await SendAsync(reviewer.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/appeal-conclusion",
            new { conclusion = "Переписка подтверждает оскорбления, решение модератора обоснованно." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Appealed", (await JsonAsync(response)).GetProperty("status").GetString());
        var after = await ChatAsync(reviewer, s.CaseId);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Equal("chat_access_denied", await CodeAsync(after));
        Assert.Equal(1, await AuditCountAsync("ChatAccessRevoked", s.ExchangeId, reviewer.Id));
        Assert.NotNull((await GrantsAsync(s.CaseId)).Single(g => g.ModeratorId == reviewer.Id).RevokedAt);
        Assert.Equal(1, await AuditCountAsync("AppealConcluded", s.CaseId, reviewer.Id));
        Assert.Contains(await NotificationsAsync(admin.Token, "AppealConclusionReady"), n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == s.CaseId);

        var detail = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/cases/{s.CaseId}"));
        Assert.Contains("обоснованно", detail.GetProperty("case").GetProperty("appeal").GetProperty("reviewerConclusion").GetString());
        var forParty = await JsonAsync(await SendAsync(s.B.Token, HttpMethod.Get, $"/cases/{s.CaseId}"));
        Assert.DoesNotContain("обоснованно", forParty.GetRawText());   // участнику заключение проверяющего не показывается
    }

    [Fact]
    public async Task The_conclusion_is_for_the_assigned_reviewer_written_once_and_long_enough()
    {
        var s = await AppealedAsync();
        var admin = await AdminAsync();
        var reviewer = await ModeratorAsync();
        var other = await ModeratorAsync();
        var body = new { conclusion = "Решение модератора обоснованно, нарушения подтверждены." };
        await AssignAsync(admin, s.CaseId, reviewer.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/appeal-conclusion", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(s.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/appeal-conclusion", body)).StatusCode);
        var shortText = await SendAsync(reviewer.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/appeal-conclusion", new { conclusion = "мало" });
        Assert.Equal("appeal_conclusion_too_short", await CodeAsync(shortText));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(admin.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/appeal-conclusion", body)).StatusCode);
        Assert.Equal(CaseStatus.AppealReview, (await CaseAsync(s.CaseId)).Status);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(reviewer.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/appeal-conclusion", body)).StatusCode);
        var twice = await SendAsync(reviewer.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/appeal-conclusion", body);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
    }

    // ======================= окончательное решение =======================

    [Fact]
    public async Task Upholding_keeps_the_decision_closes_the_case_and_tells_both_sides_by_letter()
    {
        var s = await AppealedAsync();
        var admin = await AdminAsync();

        var response = await DecideAsync(admin, s.CaseId, "Upheld", reason: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("Closed", dto.GetProperty("status").GetString());
        Assert.Equal("Warning", dto.GetProperty("decision").GetString());
        var appeal = await AppealRowAsync(s.CaseId);
        Assert.Equal(AppealOutcome.Upheld, appeal.Outcome);
        Assert.Equal(admin.Id, appeal.AdminId);
        Assert.NotNull((await CaseAsync(s.CaseId)).ClosedAt);
        Assert.Null((await WarningsAsync(s.B.Id)).Single().RevokedAt);   // предупреждение остаётся
        Assert.Equal(1, await AuditCountAsync("AppealDecided", s.CaseId, admin.Id));
        foreach (var who in new[] { s.A, s.B })
        {
            var note = Assert.Single(await NotificationsAsync(who, "AppealDecided")).GetProperty("payload");
            Assert.Equal("Upheld", note.GetProperty("outcome").GetString());
            Assert.Single(_factory.Mail.To(who.User.Email!), m => m.Subject.Contains("decision on your appeal"));
        }
    }

    [Fact]
    public async Task The_appeal_letter_goes_out_in_the_language_of_each_side()
    {
        var s = await AppealedAsync();
        var admin = await AdminAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == s.A.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.UiLanguage, "ru"));
            await db.Users.Where(u => u.Id == s.B.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.UiLanguage, "tg"));
            return 0;
        });

        await DecideAsync(admin, s.CaseId, "Overturned");

        var ru = Assert.Single(_factory.Mail.To(s.A.User.Email!), m => m.Subject.Contains("решение по апелляции"));
        Assert.Contains("решение отменено", ru.Body);
        Assert.Contains("окончательное", ru.Body);
        var tg = Assert.Single(_factory.Mail.To(s.B.User.Email!), m => m.Subject.Contains("қарор оид ба аппелятсия"));
        Assert.Contains("қарор бекор карда шуд", tg.Body);
        Assert.Contains("Огоҳии қаблан додашуда бекор карда шуд", tg.Body);   // тому, кого предупредили, сказано, что предупреждение снято
    }

    [Fact]
    public async Task Overturning_a_warning_revokes_it_and_tells_the_culprit()
    {
        var s = await AppealedAsync("Warning");
        var admin = await AdminAsync();

        var response = await DecideAsync(admin, s.CaseId, "Overturned");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("NoViolation", dto.GetProperty("decision").GetString());
        Assert.Equal(AppealOutcome.Overturned, (await AppealRowAsync(s.CaseId)).Outcome);
        Assert.NotNull((await WarningsAsync(s.B.Id)).Single().RevokedAt);
        Assert.Equal(1, await AuditCountAsync("WarningRevoked", s.B.Id, admin.Id));
        Assert.Single(await NotificationsAsync(s.B, "WarningRevoked"));
        var forB = Assert.Single(await NotificationsAsync(s.B, "AppealDecided")).GetProperty("payload");
        Assert.True(forB.GetProperty("warningRevoked").GetBoolean());
        var forA = Assert.Single(await NotificationsAsync(s.A, "AppealDecided")).GetProperty("payload");
        Assert.False(forA.GetProperty("warningRevoked").GetBoolean());
    }

    [Fact]
    public async Task Overturning_a_freeze_with_a_ban_request_rejects_the_request_and_unfreezes_the_exchange()
    {
        var s = await AppealedAsync("FrozenBanRequested");
        var admin = await AdminAsync();
        Assert.Equal(ExchangeStatus.Frozen, (await ExchangeRowAsync(s.ExchangeId)).Status);
        Assert.Equal(BanRequestStatus.Pending, Assert.Single(await BansAsync(s.B.Id)).Status);

        var response = await DecideAsync(admin, s.CaseId, "Overturned");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ban = Assert.Single(await BansAsync(s.B.Id));
        Assert.Equal(BanRequestStatus.Rejected, ban.Status);
        Assert.Equal(admin.Id, ban.DecidedById);
        var exchange = await ExchangeRowAsync(s.ExchangeId);
        Assert.Equal(ExchangeStatus.Active, exchange.Status);
        Assert.Null(exchange.FrozenAt);
        Assert.Single(await NotificationsAsync(s.A, "ExchangeUnfrozen"));
    }

    [Fact]
    public async Task Overturning_the_automatic_ban_request_goes_away_with_the_warning_that_caused_it()
    {
        var s = await TakenAsync();
        await _factory.QueryAsync(async db =>
        {
            for (var i = 0; i < 2; i++)
            {
                db.Warnings.Add(Warning.Issue(s.B.Id, s.CaseId, s.Moderator.Id, "Ранее вынесенное предупреждение за нарушение.", Now.AddMonths(-2)));
            }

            await db.SaveChangesAsync();
            return 0;
        });
        await ResolveAsync(s, "Warning");
        await AppealAsync(s.B, s.CaseId);
        Assert.Equal(BanRequestStatus.Pending, Assert.Single(await BansAsync(s.B.Id)).Status);
        var admin = await AdminAsync();

        await DecideAsync(admin, s.CaseId, "Overturned");

        Assert.Equal(BanRequestStatus.Rejected, Assert.Single(await BansAsync(s.B.Id)).Status);   // действующих предупреждений стало два
    }

    private async Task<(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid CaseId, Guid ReviewId, StaffPerson Moderator)> DisputeResolvedAsync(string decision)
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        await _factory.QueryAsync(async db =>
        {
            var session = LessonSession.Propose(x.ExchangeId, x.A.Id, x.A.Id, Now.AddHours(5), null, Now, Now.AddDays(30));
            db.Add(session);
            await db.SaveChangesAsync();
            var start = Now.AddDays(-5);
            await db.Sessions.Where(q => q.Id == session.Id).ExecuteUpdateAsync(q => q
                .SetProperty(v => v.StartAt, start).SetProperty(v => v.EndAt, start.AddMinutes(45)).SetProperty(v => v.Status, SessionStatus.Completed));
            await db.Exchanges.Where(e => e.Id == x.ExchangeId).ExecuteUpdateAsync(q => q
                .SetProperty(e => e.Status, ExchangeStatus.Completed).SetProperty(e => e.ClosedAt, Now.AddDays(-1)));
            return 0;
        });
        var body = new { scoreKnowledge = 2, scoreExplaining = 2, scorePunctuality = 2, scorePoliteness = 2, text = GoodText };
        await SendAsync(x.A.Token, HttpMethod.Post, $"/exchanges/{x.ExchangeId}/reviews", body);
        await SendAsync(x.B.Token, HttpMethod.Post, $"/exchanges/{x.ExchangeId}/reviews", body);
        var reviewId = await _factory.QueryAsync(db => db.Reviews.AsNoTracking().Where(r => r.ExchangeId == x.ExchangeId && r.AuthorId == x.B.Id).Select(r => r.Id).SingleAsync());
        var opened = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type = "ReviewDispute", category = "ReviewUnfair", exchangeId = x.ExchangeId, reviewId, description = Complaint });
        var caseId = (await JsonAsync(opened)).GetProperty("id").GetGuid();
        var moderator = await ModeratorAsync();
        await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/take");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/resolve", new { decision, alsoWarned = false, reason = Reason })).StatusCode);
        return (x.A, x.B, x.ExchangeId, caseId, reviewId, moderator);
    }

    [Fact]
    public async Task Overturning_a_hidden_review_returns_it_to_the_rating()
    {
        var d = await DisputeResolvedAsync("ReviewHidden");
        Assert.Equal(0, (await UserAsync(d.A.Id)).RatingCount);
        await AppealAsync(d.B, d.CaseId);
        var admin = await AdminAsync();

        var response = await DecideAsync(admin, d.CaseId, "Overturned");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ReviewKept", (await JsonAsync(response)).GetProperty("decision").GetString());
        var review = await _factory.QueryAsync(db => db.Reviews.AsNoTracking().SingleAsync(r => r.Id == d.ReviewId));
        Assert.Equal(ReviewStatus.Published, review.Status);
        Assert.Equal(1, (await UserAsync(d.A.Id)).RatingCount);
        Assert.Equal(1, await AuditCountAsync("ReviewRestored", d.ReviewId, admin.Id));
    }

    [Fact]
    public async Task Changing_a_kept_review_to_hidden_applies_the_new_consequences()
    {
        var d = await DisputeResolvedAsync("ReviewKept");
        Assert.Equal(1, (await UserAsync(d.A.Id)).RatingCount);
        await AppealAsync(d.A, d.CaseId);
        var admin = await AdminAsync();

        var response = await DecideAsync(admin, d.CaseId, "Changed", newDecision: "ReviewHidden", alsoWarned: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var review = await _factory.QueryAsync(db => db.Reviews.AsNoTracking().SingleAsync(r => r.Id == d.ReviewId));
        Assert.Equal(ReviewStatus.Hidden, review.Status);
        Assert.Equal(0, (await UserAsync(d.A.Id)).RatingCount);
        var warning = Assert.Single(await WarningsAsync(d.B.Id));
        Assert.Equal(admin.Id, warning.IssuedById);
        var forB = Assert.Single(await NotificationsAsync(d.B, "AppealDecided")).GetProperty("payload");
        Assert.True(forB.GetProperty("youWereWarned").GetBoolean());
        Assert.Equal("ReviewHidden", forB.GetProperty("decision").GetString());
    }

    [Fact]
    public async Task Changing_a_warning_to_a_forced_end_revokes_the_warning_and_ends_the_exchange()
    {
        var s = await AppealedAsync("Warning");
        var admin = await AdminAsync();
        var invites = Jobs.Scheduled.Count(j => j.Description.Contains("InviteAsync"));

        var response = await DecideAsync(admin, s.CaseId, "Changed", newDecision: "ExchangeEnded");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ExchangeEnded", (await JsonAsync(response)).GetProperty("decision").GetString());
        Assert.NotNull((await WarningsAsync(s.B.Id)).Single().RevokedAt);
        Assert.Equal(ExchangeStatus.Aborted, (await ExchangeRowAsync(s.ExchangeId)).Status);
        Assert.Single(await NotificationsAsync(s.A, "ExchangeAborted"));
        Assert.Equal(invites + 1, Jobs.Scheduled.Count(j => j.Description.Contains("InviteAsync")));
        Assert.Equal(AppealOutcome.Changed, (await AppealRowAsync(s.CaseId)).Outcome);
        Assert.Equal(CaseDecision.ExchangeEnded, (await AppealRowAsync(s.CaseId)).FinalDecision);
    }

    [Fact]
    public async Task Changing_no_violation_to_a_warning_issues_it()
    {
        var s = await AppealedAsync("NoViolation");
        var admin = await AdminAsync();

        await DecideAsync(admin, s.CaseId, "Changed", newDecision: "Warning");

        var warning = Assert.Single(await WarningsAsync(s.B.Id));
        Assert.Null(warning.RevokedAt);
        Assert.Equal(1, await AuditCountAsync("WarningIssued", s.B.Id, admin.Id));
    }

    [Fact]
    public async Task The_final_decision_is_validated_and_nothing_changes_on_refusal()
    {
        var s = await AppealedAsync("Warning");
        var admin = await AdminAsync();
        var moderator = await ModeratorAsync();

        var missing = await DecideAsync(admin, s.CaseId, "Changed", newDecision: null);
        var extra = await DecideAsync(admin, s.CaseId, "Upheld", newDecision: "NoViolation");
        var wrongType = await DecideAsync(admin, s.CaseId, "Changed", newDecision: "ReviewKept");
        var notCombinable = await DecideAsync(admin, s.CaseId, "Changed", newDecision: "Warning", alsoWarned: true);
        var noReason = await DecideAsync(admin, s.CaseId, "Overturned", reason: "мало");
        var byModerator = await SendAsync(moderator.Token, HttpMethod.Post, $"/admin/cases/{s.CaseId}/appeal/decision", new { outcome = "Upheld" });

        Assert.Equal("appeal_final_decision_mismatch", await CodeAsync(missing));
        Assert.Equal("appeal_final_decision_mismatch", await CodeAsync(extra));
        Assert.Equal("case_decision_not_allowed", await CodeAsync(wrongType));
        Assert.Equal("case_warning_not_combinable", await CodeAsync(notCombinable));
        Assert.Equal("case_reason_too_short", await CodeAsync(noReason));
        Assert.Equal(HttpStatusCode.Forbidden, byModerator.StatusCode);
        Assert.Equal(CaseStatus.Appealed, (await CaseAsync(s.CaseId)).Status);
        Assert.Null((await WarningsAsync(s.B.Id)).Single().RevokedAt);
        Assert.Empty(await NotificationsAsync(s.A, "AppealDecided"));
    }

    [Fact]
    public async Task There_is_nothing_to_overturn_in_a_neutral_decision_and_a_decision_is_final()
    {
        var s = await AppealedAsync("NoViolation");
        var admin = await AdminAsync();

        var nothing = await DecideAsync(admin, s.CaseId, "Overturned");
        Assert.Equal(HttpStatusCode.Conflict, nothing.StatusCode);
        Assert.Equal("nothing_to_overturn", await CodeAsync(nothing));

        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(admin, s.CaseId, "Upheld", reason: null)).StatusCode);
        var again = await DecideAsync(admin, s.CaseId, "Changed", newDecision: "Warning");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(again));
        Assert.Empty(await WarningsAsync(s.B.Id));
    }

    [Fact]
    public async Task The_decision_closes_every_chat_access_left_on_the_case()
    {
        var s = await AppealedAsync();
        var admin = await AdminAsync();

        await DecideAsync(admin, s.CaseId, "Upheld", reason: null);

        Assert.All(await GrantsAsync(s.CaseId), g => Assert.NotNull(g.RevokedAt));
        Assert.Equal(HttpStatusCode.Forbidden, (await ChatAsync(s.Moderator, s.CaseId)).StatusCode);
    }

    // ======================= запросы на блокировку =======================

    private async Task<(Scenario S, BanRequest Ban)> PendingBanAsync()
    {
        var s = await TakenAsync();
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(s, "FrozenBanRequested")).StatusCode);
        return (s, Assert.Single(await BansAsync(s.B.Id)));
    }

    [Fact]
    public async Task The_admin_lists_ban_requests_by_status()
    {
        var (s, ban) = await PendingBanAsync();
        var admin = await AdminAsync();

        var pending = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/ban-requests?status=Pending&pageSize=50"));
        var approved = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/ban-requests?status=Approved&pageSize=50"));

        var item = pending.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == ban.Id);
        Assert.Equal(s.B.Id, item.GetProperty("userId").GetGuid());
        Assert.Equal(s.CaseId, item.GetProperty("caseId").GetGuid());
        Assert.Equal(s.Moderator.Id, item.GetProperty("requestedById").GetGuid());
        Assert.DoesNotContain(approved.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == ban.Id);
        Assert.Equal("invalid_paging", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/ban-requests?pageSize=500")));
    }

    [Fact]
    public async Task Approving_a_ban_blocks_the_account_revokes_sessions_and_aborts_exchanges_neutrally()
    {
        var (s, ban) = await PendingBanAsync();   // обмен заморожен
        var admin = await AdminAsync();
        var lessonId = await _factory.QueryAsync(async db =>
        {
            var lesson = LessonSession.Propose(s.ExchangeId, s.A.Id, s.A.Id, Now.AddDays(2), null, Now, Now.AddDays(30));
            db.Add(lesson);
            db.Set<RefreshToken>().Add(RefreshToken.Create(s.B.Id, Guid.NewGuid().ToString("N"), Now.AddDays(10), "test", null, Guid.NewGuid(), Now));
            db.Set<RefreshToken>().Add(RefreshToken.Create(s.B.Id, Guid.NewGuid().ToString("N"), Now.AddDays(10), "test", null, Guid.NewGuid(), Now));
            await db.SaveChangesAsync();
            return lesson.Id;
        });
        var invites = Jobs.Scheduled.Count(j => j.Description.Contains("InviteAsync"));

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/approve", new { comment = "Подтверждаю" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Approved", (await JsonAsync(response)).GetProperty("status").GetString());
        Assert.Equal(UserStatus.Blocked, (await UserAsync(s.B.Id)).Status);
        var tokens = await _factory.QueryAsync(db => db.Set<RefreshToken>().AsNoTracking().Where(t => t.UserId == s.B.Id).ToListAsync());
        Assert.NotEmpty(tokens);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));   // сеансы отозваны

        var exchange = await ExchangeRowAsync(s.ExchangeId);
        Assert.Equal(ExchangeStatus.Aborted, exchange.Status);
        Assert.Equal("account_blocked", exchange.AbortReason);
        Assert.Equal(SessionStatus.Cancelled, (await _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(x => x.Id == lessonId))).Status);
        var partnerView = await JsonAsync(await SendAsync(s.A.Token, HttpMethod.Get, $"/exchanges/{s.ExchangeId}"));
        Assert.Equal("partner_unavailable", partnerView.GetProperty("abortReason").GetString());   // партнёр видит нейтральную причину
        var note = Assert.Single(await NotificationsAsync(s.A, "ExchangeAborted")).GetProperty("payload");
        Assert.Equal("partner_unavailable", note.GetProperty("reason").GetString());
        Assert.Equal(invites + 1, Jobs.Scheduled.Count(j => j.Description.Contains("InviteAsync")));

        Assert.Equal(1, await AuditCountAsync("BanDecided", s.B.Id, admin.Id));
        Assert.Single(await NotificationsAsync(s.Moderator.Token, "BanRequestDecided"), n => n.GetProperty("payload").GetProperty("approved").GetBoolean());

        // Токен заблокированного ещё жив, но сервисы его не пускают.
        var afterBlock = await SendAsync(s.B.Token, HttpMethod.Get, "/me/cases");
        Assert.Equal(HttpStatusCode.Forbidden, afterBlock.StatusCode);
        Assert.Equal("account_not_active", await CodeAsync(afterBlock));
    }

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

    [Fact]
    public async Task Approving_a_ban_drops_the_open_realtime_connections_of_the_user_and_refuses_new_ones()
    {
        var (s, ban) = await PendingBanAsync();
        var admin = await AdminAsync();
        var registry = _factory.Services.GetRequiredService<ConnectionRegistry>();
        var banned = new FakeHubContext(s.B.Id);
        var partner = new FakeHubContext(s.A.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var profiles = scope.ServiceProvider.GetRequiredService<SkillSwap.Application.Profile.IProfileStore>();
            Assert.True(await registry.AcceptAsync(banned, profiles));
            Assert.True(await registry.AcceptAsync(partner, profiles));
        }

        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/approve");

        Assert.True(banned.Aborted);
        Assert.False(partner.Aborted);   // чужие соединения не трогаются
        Assert.Equal(0, registry.CountFor(s.B.Id));
        var late = new FakeHubContext(s.B.Id);
        using var again = _factory.Services.CreateScope();
        Assert.False(await registry.AcceptAsync(late, again.ServiceProvider.GetRequiredService<SkillSwap.Application.Profile.IProfileStore>()));
        Assert.True(late.Aborted);   // заблокированный с ещё живым токеном подключиться не может
    }

    /// <summary>Новый пользователь с подходящими навыками и ожидающая заявка на обмен (в одну или в другую сторону) между ним и <paramref name="target"/>.</summary>
    private async Task<(Guid Id, MatchPerson Other)> PendingRequestAsync(MatchTestWorld world, MatchPerson target, int targetTeaches, bool toTarget)
    {
        var learns = 1 - targetTeaches;   // у второго человека навыки зеркальные: он преподаёт то, что хочет целевой, и наоборот
        var other = await world.UserAsync((learns, SkillLevel.Beginner), (targetTeaches, SkillLevel.Beginner));
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == other.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var response = toTarget
            ? await SendAsync(other.Token, HttpMethod.Post, "/exchange-requests", new { toUserId = target.Id, toTeachesSkillId = world.S(targetTeaches), fromTeachesSkillId = world.S(learns), durationDays = 30 })
            : await SendAsync(target.Token, HttpMethod.Post, "/exchange-requests", new { toUserId = other.Id, toTeachesSkillId = world.S(learns), fromTeachesSkillId = world.S(targetTeaches), durationDays = 30 });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return ((await JsonAsync(response)).GetProperty("id").GetGuid(), other);
    }

    [Fact]
    public async Task Approving_a_ban_expires_the_pending_requests_of_the_user_in_both_directions_without_telling_the_senders()
    {
        var (s, ban) = await PendingBanAsync();
        var admin = await AdminAsync();
        var (incoming, sender) = await PendingRequestAsync(s.World, s.B, 1, toTarget: true);
        var (outgoing, receiver) = await PendingRequestAsync(s.World, s.B, 1, toTarget: false);
        var (unrelated, _) = await PendingRequestAsync(s.World, s.A, 0, toTarget: true);   // чужая заявка к A не трогается

        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/approve");

        var states = await _factory.QueryAsync(db => db.ExchangeRequests.AsNoTracking().Where(r => r.Id == incoming || r.Id == outgoing || r.Id == unrelated).ToDictionaryAsync(r => r.Id, r => r.Status));
        Assert.Equal(ExchangeRequestStatus.Expired, states[incoming]);
        Assert.Equal(ExchangeRequestStatus.Expired, states[outgoing]);
        Assert.Equal(ExchangeRequestStatus.Pending, states[unrelated]);
        Assert.Empty(await NotificationsAsync(sender, "RequestExpired"));    // отправителям причина не раскрывается
        Assert.Empty(await NotificationsAsync(receiver, "RequestExpired"));
    }

    [Theory]
    [InlineData("ru", "аккаунт заблокирован", "за нарушение правил платформы")]
    [InlineData("tg", "аккаунт масдуд карда шуд", "барои вайрон кардани қоидаҳои платформа")]
    [InlineData("en", "your account has been blocked", "for violating the platform rules")]
    public async Task The_blocked_user_gets_a_letter_in_their_language_without_case_details(string language, string subject, string phrase)
    {
        var (s, ban) = await PendingBanAsync();
        var admin = await AdminAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == s.B.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.UiLanguage, language));
            return 0;
        });

        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/approve", new { comment = "Внутренний комментарий админа" });

        var letter = Assert.Single(_factory.Mail.To(s.B.User.Email!), m => m.Subject.Contains(subject));
        Assert.Contains(phrase, letter.Body);
        Assert.Contains(s.B.User.Name, letter.Body);
        Assert.DoesNotContain("оскорбля", letter.Body);          // деталей обращения нет
        Assert.DoesNotContain("Внутренний комментарий", letter.Body);
        Assert.DoesNotContain(s.CaseId.ToString(), letter.Body);
    }

    [Fact]
    public async Task A_rejected_ban_request_sends_no_letter()
    {
        var (s, ban) = await PendingBanAsync();
        var admin = await AdminAsync();
        var before = _factory.Mail.To(s.B.User.Email!).Count;

        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/reject");

        Assert.Equal(before, _factory.Mail.To(s.B.User.Email!).Count);
    }

    [Fact]
    public async Task A_ban_request_is_decided_once_and_by_an_admin()
    {
        var (s, ban) = await PendingBanAsync();
        var admin = await AdminAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(s.Moderator.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(s.A.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/reject")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{Guid.NewGuid()}/approve")).StatusCode);
        Assert.Equal(UserStatus.Active, (await UserAsync(s.B.Id)).Status);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/approve")).StatusCode);
        var again = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/approve");
        var reject = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/reject");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, reject.StatusCode);
        Assert.Equal(1, await AuditCountAsync("BanDecided", s.B.Id, admin.Id));
    }

    [Fact]
    public async Task A_second_request_for_an_already_blocked_user_fails_and_stays_pending()
    {
        var (s, ban) = await PendingBanAsync();
        var admin = await AdminAsync();
        var second = await _factory.QueryAsync(async db =>
        {
            var extra = BanRequest.ByModerator(s.B.Id, s.CaseId, s.Moderator.Id, Reason);
            db.BanRequests.Add(extra);
            await db.SaveChangesAsync();
            return extra.Id;
        });
        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/approve");

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{second}/approve");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("user_not_active", await CodeAsync(response));
        Assert.Equal(BanRequestStatus.Pending, (await _factory.QueryAsync(db => db.BanRequests.AsNoTracking().SingleAsync(b => b.Id == second))).Status);
    }

    [Fact]
    public async Task Rejecting_a_ban_request_unfreezes_the_exchange_when_no_other_complaint_holds_it()
    {
        var (s, ban) = await PendingBanAsync();
        var admin = await AdminAsync();
        Assert.Equal(ExchangeStatus.Frozen, (await ExchangeRowAsync(s.ExchangeId)).Status);

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/reject", new { comment = "Недостаточно оснований" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Rejected", (await JsonAsync(response)).GetProperty("status").GetString());
        var exchange = await ExchangeRowAsync(s.ExchangeId);
        Assert.Equal(ExchangeStatus.Active, exchange.Status);
        Assert.Equal(UserStatus.Active, (await UserAsync(s.B.Id)).Status);
        Assert.Single(await NotificationsAsync(s.A, "ExchangeUnfrozen"));
        Assert.Single(await NotificationsAsync(s.Moderator.Token, "BanRequestDecided"), n => !n.GetProperty("payload").GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task Rejecting_a_ban_request_keeps_the_exchange_frozen_while_another_complaint_is_open()
    {
        var (s, ban) = await PendingBanAsync();
        var admin = await AdminAsync();
        var counter = await SendAsync(s.B.Token, HttpMethod.Post, "/cases", new { type = "Conduct", category = "Threats", exchangeId = s.ExchangeId, description = Complaint });
        Assert.Equal(HttpStatusCode.Created, counter.StatusCode);   // встречная жалоба по тому же обмену

        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/ban-requests/{ban.Id}/reject");

        Assert.Equal(ExchangeStatus.Frozen, (await ExchangeRowAsync(s.ExchangeId)).Status);
    }
}
