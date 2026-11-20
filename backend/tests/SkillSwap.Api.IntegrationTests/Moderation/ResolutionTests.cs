using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Account;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>Решения по обращениям (раздел 8.3 ТЗ): последствия каждого решения, отзыв доступа к чату, уведомления и письма, предупреждения и бан, закрытие через 7 дней.</summary>
[Collection(ApiCollection.Name)]
public class ResolutionTests
{
    private const string Complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";
    private const string Reason = "Переписка подтверждает жалобу: оскорбления были, договорённости сорваны.";
    private const string GoodText = "Очень понятно объясняет, уроки были полезными и по делу.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ResolutionTests(ApiFactory factory)
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

    private Task<HttpResponseMessage> ResolveAsync(StaffPerson moderator, Guid caseId, string decision, bool alsoWarned = false, string? reason = Reason) =>
        SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/resolve", new { decision, alsoWarned, reason });

    private Task<HttpResponseMessage> ChatAsync(StaffPerson moderator, Guid caseId) => SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{caseId}/chat");

    private async Task<Guid> FileAsync(MatchPerson who, Guid exchangeId, string type = "Conduct", string category = "Insults")
    {
        var response = await SendAsync(who.Token, HttpMethod.Post, "/cases", new { type, category, exchangeId, description = Complaint });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    /// <summary>Жалоба A на B, взятая модератором.</summary>
    private async Task<(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid CaseId, StaffPerson Moderator, MatchTestWorld World)> TakenAsync(string type = "Conduct", string category = "Insults")
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var caseId = await FileAsync(x.A, x.ExchangeId, type, category);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/take")).StatusCode);
        return (x.A, x.B, x.ExchangeId, caseId, moderator, x.World);
    }

    private async Task<List<JsonElement>> NotificationsAsync(MatchPerson who, string type)
    {
        var items = (await JsonAsync(await SendAsync(who.Token, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return items.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    private Task<Case> CaseAsync(Guid id) => _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task<Exchange> ExchangeRowAsync(Guid id) => _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == id));

    private Task<User> UserAsync(Guid id) => _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<int> AuditCountAsync(string action, Guid? target = null, Guid? actor = null) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == action && (target == null || a.TargetId == target) && (actor == null || a.ActorId == actor)));

    private Task<Warning[]> WarningsAsync(Guid userId) => _factory.QueryAsync(db => db.Warnings.AsNoTracking().Where(w => w.UserId == userId).ToArrayAsync());

    private Task<BanRequest[]> BanRequestsAsync(Guid userId) => _factory.QueryAsync(db => db.BanRequests.AsNoTracking().Where(b => b.UserId == userId).ToArrayAsync());

    // ---------- сквозной: взял → прочитал чат → решил → чат закрыт ----------

    [Fact]
    public async Task End_to_end_taking_reading_the_chat_and_deciding_closes_the_chat_with_403_and_an_audit_record()
    {
        var x = await TakenAsync();
        await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Сообщение, которое проверяет модератор");
        var read = await ChatAsync(x.Moderator, x.CaseId);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(0, await AuditCountAsync(AuditActions.ChatAccessRevoked, x.ExchangeId, x.Moderator.Id));

        var resolved = await ResolveAsync(x.Moderator, x.CaseId, "Warning");

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal("Resolved", (await JsonAsync(resolved)).GetProperty("status").GetString());
        var after = await ChatAsync(x.Moderator, x.CaseId);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        Assert.Equal("chat_access_denied", await CodeAsync(after));
        Assert.Equal(1, await AuditCountAsync(AuditActions.ChatAccessRevoked, x.ExchangeId, x.Moderator.Id));
        var grant = await _factory.QueryAsync(db => db.ChatAccessGrants.AsNoTracking().SingleAsync(g => g.CaseId == x.CaseId));
        Assert.NotNull(grant.RevokedAt);
        var card = await JsonAsync(await SendAsync(x.Moderator.Token, HttpMethod.Get, $"/moderation/cases/{x.CaseId}"));
        Assert.False(card.GetProperty("hasChatAccess").GetBoolean());
        Assert.Equal(1, await AuditCountAsync(AuditActions.CaseResolved, x.CaseId, x.Moderator.Id));
    }

    // ---------- общее: решение, уведомления, срок апелляции ----------

    [Fact]
    public async Task A_warning_is_recorded_for_the_culprit_and_both_sides_are_notified_with_the_appeal_deadline()
    {
        var x = await TakenAsync();

        var response = await ResolveAsync(x.Moderator, x.CaseId, "Warning");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("Warning", dto.GetProperty("decision").GetString());
        Assert.Equal(Reason, dto.GetProperty("decisionReason").GetString());
        var stored = await CaseAsync(x.CaseId);
        Assert.Equal(CaseStatus.Resolved, stored.Status);
        Assert.NotNull(stored.ResolvedAt);

        var warning = Assert.Single(await WarningsAsync(x.B.Id));
        Assert.Equal(x.CaseId, warning.CaseId);
        Assert.Equal(x.Moderator.Id, warning.IssuedById);
        Assert.InRange((warning.ExpiresAt - Now).TotalDays, 360, 370);
        Assert.Equal(1, await AuditCountAsync(AuditActions.WarningIssued, x.B.Id));
        Assert.Single(await NotificationsAsync(x.B, "WarningIssued"));
        Assert.Empty(await NotificationsAsync(x.A, "WarningIssued"));

        var forA = Assert.Single(await NotificationsAsync(x.A, "CaseResolved")).GetProperty("payload");
        var forB = Assert.Single(await NotificationsAsync(x.B, "CaseResolved")).GetProperty("payload");
        Assert.Equal("Opener", forA.GetProperty("role").GetString());
        Assert.False(forA.GetProperty("youWereWarned").GetBoolean());
        Assert.True(forB.GetProperty("youWereWarned").GetBoolean());
        Assert.Equal("Warning", forB.GetProperty("decision").GetString());
        Assert.InRange((forB.GetProperty("appealUntil").GetDateTime() - Now).TotalDays, 6.9, 7.1);

        foreach (var who in new[] { x.A, x.B })   // решение видят обе стороны
        {
            var own = await JsonAsync(await SendAsync(who.Token, HttpMethod.Get, $"/cases/{x.CaseId}"));
            Assert.Equal("Warning", own.GetProperty("decision").GetString());
        }
    }

    [Fact]
    public async Task The_decision_letter_goes_to_both_sides_in_their_own_language()
    {
        var x = await TakenAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == x.A.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.UiLanguage, "ru"));
            await db.Users.Where(u => u.Id == x.B.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.UiLanguage, "tg"));
            return 0;
        });

        await ResolveAsync(x.Moderator, x.CaseId, "NoViolation");

        var letterA = Assert.Single(_factory.Mail.To(x.A.User.Email!), m => m.Subject.Contains("решение по обращению"));
        Assert.Contains("нарушений не найдено", letterA.Body);
        Assert.Contains($"/cases/{x.CaseId}", letterA.Body);
        Assert.Contains("апелляцию", letterA.Body);
        var letterB = Assert.Single(_factory.Mail.To(x.B.User.Email!), m => m.Subject.Contains("қарор оид ба муроҷиат"));
        Assert.Contains("вайронкунӣ ёфт нашуд", letterB.Body);
    }

    [Fact]
    public async Task The_letter_defaults_to_english_and_mentions_an_added_warning()
    {
        var x = await TakenAsync();

        await ResolveAsync(x.Moderator, x.CaseId, "ExchangeEnded", alsoWarned: true);

        var letter = Assert.Single(_factory.Mail.To(x.A.User.Email!), m => m.Subject.Contains("decision on your case"));
        Assert.Contains("ended by a moderator", letter.Body);
        Assert.Contains("warning", letter.Body);
    }

    [Fact]
    public async Task Resolving_queues_the_closing_job_for_seven_days_later()
    {
        var x = await TakenAsync();
        var before = Jobs.Scheduled.Count(j => j.Description.Contains("CloseIfDueAsync"));

        await ResolveAsync(x.Moderator, x.CaseId, "NoViolation");

        var jobs = Jobs.Scheduled.Where(j => j.Description.Contains("CloseIfDueAsync")).ToList();
        Assert.Equal(before + 1, jobs.Count);
        Assert.InRange((jobs[^1].RunAt.UtcDateTime - Now).TotalDays, 6.9, 7.1);
    }

    // ---------- проверки ----------

    [Fact]
    public async Task A_decision_that_does_not_fit_the_case_is_rejected_and_nothing_changes()
    {
        var x = await TakenAsync();

        var wrongType = await ResolveAsync(x.Moderator, x.CaseId, "ReviewKept");
        var notCombinable = await ResolveAsync(x.Moderator, x.CaseId, "Warning", alsoWarned: true);
        var shortReason = await ResolveAsync(x.Moderator, x.CaseId, "NoViolation", reason: "мало");
        var noReason = await ResolveAsync(x.Moderator, x.CaseId, "NoViolation", reason: null);

        Assert.Equal("case_decision_not_allowed", await CodeAsync(wrongType));
        Assert.Equal("case_warning_not_combinable", await CodeAsync(notCombinable));
        Assert.Equal("case_reason_too_short", await CodeAsync(shortReason));
        Assert.Equal("case_reason_too_short", await CodeAsync(noReason));
        Assert.Equal(CaseStatus.InProgress, (await CaseAsync(x.CaseId)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ChatAsync(x.Moderator, x.CaseId)).StatusCode);   // доступ не отозван
        Assert.Empty(await WarningsAsync(x.B.Id));
        Assert.Empty(await NotificationsAsync(x.A, "CaseResolved"));
    }

    [Fact]
    public async Task Only_the_owning_moderator_can_decide_and_only_once()
    {
        var x = await TakenAsync();
        var other = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);

        Assert.Equal(HttpStatusCode.NotFound, (await ResolveAsync(other, x.CaseId, "NoViolation")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ResolveAsync(x.Moderator, Guid.NewGuid(), "NoViolation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(admin.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/resolve", new { decision = "NoViolation", reason = Reason })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(x.A.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/resolve", new { decision = "NoViolation", reason = Reason })).StatusCode);
        Assert.Equal(CaseStatus.InProgress, (await CaseAsync(x.CaseId)).Status);

        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(x.Moderator, x.CaseId, "NoViolation")).StatusCode);
        var again = await ResolveAsync(x.Moderator, x.CaseId, "Warning");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(again));
        Assert.Single(await NotificationsAsync(x.A, "CaseResolved"));
        Assert.Empty(await WarningsAsync(x.B.Id));
    }

    [Fact]
    public async Task A_decision_cannot_be_made_while_explanations_are_awaited()
    {
        var x = await TakenAsync();
        await SendAsync(x.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/questions", new { recipient = "Both", text = "Опишите, что произошло." });

        var response = await ResolveAsync(x.Moderator, x.CaseId, "NoViolation");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("waiting_for_explanations", await CodeAsync(response));
        Assert.Equal(CaseStatus.WaitingResponse, (await CaseAsync(x.CaseId)).Status);
    }

    // ---------- спор по отзыву ----------

    /// <summary>Обмен, где оба написали отзыв (опубликованы сразу), и спор A об отзыве B, взятый модератором.</summary>
    private async Task<(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid CaseId, Guid ReviewId, StaffPerson Moderator)> TakenDisputeAsync()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        await _factory.QueryAsync(async db =>
        {
            var session = LessonSession.Propose(x.ExchangeId, x.A.Id, x.A.Id, Now.AddHours(5), null, Now, Now.AddDays(30));
            db.Add(session);
            await db.SaveChangesAsync();
            var start = Now.AddDays(-5);
            await db.Sessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(s => s
                .SetProperty(v => v.StartAt, start).SetProperty(v => v.EndAt, start.AddMinutes(45)).SetProperty(v => v.Status, SessionStatus.Completed));
            await db.Exchanges.Where(e => e.Id == x.ExchangeId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExchangeStatus.Completed).SetProperty(e => e.ClosedAt, Now.AddDays(-1)));
            return 0;
        });
        var body = new { scoreKnowledge = 2, scoreExplaining = 2, scorePunctuality = 2, scorePoliteness = 2, text = GoodText };
        await SendAsync(x.A.Token, HttpMethod.Post, $"/exchanges/{x.ExchangeId}/reviews", body);
        await SendAsync(x.B.Token, HttpMethod.Post, $"/exchanges/{x.ExchangeId}/reviews", body);
        var reviewId = await _factory.QueryAsync(db => db.Reviews.AsNoTracking().Where(r => r.ExchangeId == x.ExchangeId && r.AuthorId == x.B.Id).Select(r => r.Id).SingleAsync());
        var opened = await SendAsync(x.A.Token, HttpMethod.Post, "/cases",
            new { type = "ReviewDispute", category = "ReviewUnfair", exchangeId = x.ExchangeId, reviewId, description = Complaint });
        var caseId = (await JsonAsync(opened)).GetProperty("id").GetGuid();
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/take");
        return (x.A, x.B, x.ExchangeId, caseId, reviewId, moderator);
    }

    private Task<Review> ReviewAsync(Guid id) => _factory.QueryAsync(db => db.Reviews.AsNoTracking().SingleAsync(r => r.Id == id));

    [Fact]
    public async Task Keeping_the_review_returns_it_to_the_rating()
    {
        var x = await TakenDisputeAsync();
        Assert.Equal(0, (await UserAsync(x.A.Id)).RatingCount);   // оспариваемый отзыв в рейтинг не входит

        var response = await ResolveAsync(x.Moderator, x.CaseId, "ReviewKept");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ReviewStatus.Published, (await ReviewAsync(x.ReviewId)).Status);
        Assert.Equal(1, (await UserAsync(x.A.Id)).RatingCount);
        Assert.Empty(await WarningsAsync(x.B.Id));
    }

    [Fact]
    public async Task Hiding_the_review_removes_it_from_the_rating_and_the_public_list()
    {
        var x = await TakenDisputeAsync();

        var response = await ResolveAsync(x.Moderator, x.CaseId, "ReviewHidden");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ReviewStatus.Hidden, (await ReviewAsync(x.ReviewId)).Status);
        Assert.Equal(0, (await UserAsync(x.A.Id)).RatingCount);
        var list = await JsonAsync(await SendAsync(x.B.Token, HttpMethod.Get, $"/users/{x.A.Id}/reviews"));
        Assert.Equal(0, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, await AuditCountAsync(AuditActions.ReviewHidden, x.ReviewId));
        Assert.Empty(await WarningsAsync(x.B.Id));
    }

    [Fact]
    public async Task Hiding_a_review_with_a_warning_warns_its_author()
    {
        var x = await TakenDisputeAsync();

        await ResolveAsync(x.Moderator, x.CaseId, "ReviewHidden", alsoWarned: true);

        var warning = Assert.Single(await WarningsAsync(x.B.Id));   // автор отзыва — тот, на кого подан спор
        Assert.Equal(x.CaseId, warning.CaseId);
        Assert.Equal(ReviewStatus.Hidden, (await ReviewAsync(x.ReviewId)).Status);
    }

    // ---------- принудительное завершение ----------

    [Fact]
    public async Task Ending_the_exchange_closes_it_cancels_lessons_notifies_both_and_queues_review_invitations()
    {
        var x = await TakenAsync();
        var lessonId = await _factory.QueryAsync(async db =>
        {
            var s = LessonSession.Propose(x.ExchangeId, x.A.Id, x.A.Id, Now.AddDays(2), null, Now, Now.AddDays(30));
            db.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });
        var invites = Jobs.Scheduled.Count(j => j.Description.Contains("InviteAsync"));

        var response = await ResolveAsync(x.Moderator, x.CaseId, "ExchangeEnded");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var exchange = await ExchangeRowAsync(x.ExchangeId);
        Assert.Equal(ExchangeStatus.Aborted, exchange.Status);
        Assert.Equal("moderation", exchange.AbortReason);
        Assert.NotNull(exchange.ClosedAt);
        Assert.Equal(SessionStatus.Cancelled, (await _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == lessonId))).Status);
        Assert.Single(await NotificationsAsync(x.A, "ExchangeAborted"));
        Assert.Single(await NotificationsAsync(x.B, "ExchangeAborted"));
        Assert.Equal(1, await AuditCountAsync(AuditActions.ExchangeForceEnded, x.ExchangeId, x.Moderator.Id));
        Assert.Equal(invites + 1, Jobs.Scheduled.Count(j => j.Description.Contains("InviteAsync")));
        Assert.Empty(await WarningsAsync(x.B.Id));
    }

    [Fact]
    public async Task Ending_a_frozen_exchange_works_and_an_already_closed_one_is_refused_without_side_effects()
    {
        var frozen = await TakenAsync();
        await SendAsync(frozen.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{frozen.CaseId}/freeze-exchange");
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(frozen.Moderator, frozen.CaseId, "ExchangeEnded", alsoWarned: true)).StatusCode);
        Assert.Equal(ExchangeStatus.Aborted, (await ExchangeRowAsync(frozen.ExchangeId)).Status);
        Assert.Single(await WarningsAsync(frozen.B.Id));

        var closed = await TakenAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == closed.ExchangeId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExchangeStatus.Completed).SetProperty(e => e.ClosedAt, Now.AddDays(-1)));
            return 0;
        });
        var response = await ResolveAsync(closed.Moderator, closed.CaseId, "ExchangeEnded");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("exchange_not_open", await CodeAsync(response));
        Assert.Equal(CaseStatus.InProgress, (await CaseAsync(closed.CaseId)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ChatAsync(closed.Moderator, closed.CaseId)).StatusCode);
    }

    // ---------- заморозка и запрос на бан ----------

    [Fact]
    public async Task Freezing_with_a_ban_request_freezes_the_exchange_and_creates_a_pending_request_for_the_admin()
    {
        var x = await TakenAsync();

        var response = await ResolveAsync(x.Moderator, x.CaseId, "FrozenBanRequested");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExchangeStatus.Frozen, (await ExchangeRowAsync(x.ExchangeId)).Status);
        var ban = Assert.Single(await BanRequestsAsync(x.B.Id));
        Assert.Equal(BanRequestStatus.Pending, ban.Status);
        Assert.Equal(x.CaseId, ban.CaseId);
        Assert.Equal(x.Moderator.Id, ban.RequestedById);
        Assert.Equal(Reason, ban.Reason);
        Assert.Equal(1, await AuditCountAsync(AuditActions.BanRequested, x.B.Id));
        Assert.Equal(UserStatus.Active, (await UserAsync(x.B.Id)).Status);   // блокирует только админ
    }

    [Fact]
    public async Task A_frozen_exchange_stays_frozen_after_a_ban_request_decision_and_is_released_by_a_warning_or_no_violation()
    {
        var banned = await TakenAsync();
        await SendAsync(banned.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{banned.CaseId}/freeze-exchange");
        await ResolveAsync(banned.Moderator, banned.CaseId, "FrozenBanRequested");
        Assert.Equal(ExchangeStatus.Frozen, (await ExchangeRowAsync(banned.ExchangeId)).Status);

        foreach (var decision in new[] { "Warning", "NoViolation" })
        {
            var x = await TakenAsync();
            await SendAsync(x.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/freeze-exchange");
            Assert.Equal(ExchangeStatus.Frozen, (await ExchangeRowAsync(x.ExchangeId)).Status);

            await ResolveAsync(x.Moderator, x.CaseId, decision);

            var exchange = await ExchangeRowAsync(x.ExchangeId);
            Assert.Equal(ExchangeStatus.Active, exchange.Status);
            Assert.Null(exchange.FrozenAt);
            Assert.Single(await NotificationsAsync(x.A, "ExchangeUnfrozen"));
        }
    }

    [Fact]
    public async Task Another_open_complaint_keeps_the_exchange_frozen_when_one_of_them_is_decided_without_consequences()
    {
        var x = await TakenAsync();
        var second = await FileAsync(x.B, x.ExchangeId);   // встречная жалоба по тому же обмену
        await SendAsync(x.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/freeze-exchange");

        await ResolveAsync(x.Moderator, x.CaseId, "NoViolation");

        Assert.Equal(ExchangeStatus.Frozen, (await ExchangeRowAsync(x.ExchangeId)).Status);
        Assert.NotEqual(Guid.Empty, second);
    }

    // ---------- предупреждения и автоматический запрос на бан ----------

    private Task AddWarningsAsync(Guid userId, Guid caseId, Guid moderatorId, int active, int revoked = 0) => _factory.QueryAsync(async db =>
    {
        for (var i = 0; i < active + revoked; i++)
        {
            db.Warnings.Add(Warning.Issue(userId, caseId, moderatorId, "Ранее вынесенное предупреждение за нарушение.", Now.AddMonths(-2)));
        }

        await db.SaveChangesAsync();
        if (revoked > 0)
        {
            var ids = await db.Warnings.Where(w => w.UserId == userId && w.RevokedAt == null).OrderBy(w => w.Id).Take(revoked).Select(w => w.Id).ToListAsync();
            await db.Warnings.Where(w => ids.Contains(w.Id)).ExecuteUpdateAsync(s => s.SetProperty(w => w.RevokedAt, Now));
        }

        return 0;
    });

    [Fact]
    public async Task The_third_active_warning_creates_an_automatic_ban_request_once()
    {
        var x = await TakenAsync();
        await AddWarningsAsync(x.B.Id, x.CaseId, x.Moderator.Id, active: 2);

        await ResolveAsync(x.Moderator, x.CaseId, "Warning");

        var ban = Assert.Single(await BanRequestsAsync(x.B.Id));
        Assert.Null(ban.RequestedById);   // запрос создала система
        Assert.Equal(BanRequestStatus.Pending, ban.Status);

        // Четвёртое предупреждение при необработанном запросе второй запрос не создаёт.
        var more = await FileAsync(x.A, x.ExchangeId, "NoShowAbuse", "SystematicEvasion");
        await SendAsync(x.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{more}/take");
        await ResolveAsync(x.Moderator, more, "Warning");
        Assert.Single(await BanRequestsAsync(x.B.Id));
        Assert.Equal(4, (await WarningsAsync(x.B.Id)).Length);
    }

    [Fact]
    public async Task Revoked_warnings_do_not_count_towards_the_ban_request()
    {
        var x = await TakenAsync();
        await AddWarningsAsync(x.B.Id, x.CaseId, x.Moderator.Id, active: 1, revoked: 1);

        await ResolveAsync(x.Moderator, x.CaseId, "Warning");

        Assert.Empty(await BanRequestsAsync(x.B.Id));
    }

    [Fact]
    public async Task Expired_warnings_do_not_count_towards_the_ban_request()
    {
        var x = await TakenAsync();
        await AddWarningsAsync(x.B.Id, x.CaseId, x.Moderator.Id, active: 2);
        await _factory.QueryAsync(async db =>
        {
            await db.Warnings.Where(w => w.UserId == x.B.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.ExpiresAt, Now.AddDays(-1)));
            return 0;
        });

        await ResolveAsync(x.Moderator, x.CaseId, "Warning");

        Assert.Empty(await BanRequestsAsync(x.B.Id));
    }

    // ---------- заморозка и разморозка вручную ----------

    [Fact]
    public async Task The_moderator_freezes_and_unfreezes_the_exchange_by_hand()
    {
        var x = await TakenAsync();

        var frozen = await SendAsync(x.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/freeze-exchange");
        Assert.Equal(HttpStatusCode.OK, frozen.StatusCode);
        Assert.Equal("Frozen", (await JsonAsync(frozen)).GetProperty("status").GetString());
        Assert.Equal(ExchangeStatus.Frozen, (await ExchangeRowAsync(x.ExchangeId)).Status);
        Assert.Single(await NotificationsAsync(x.A, "ExchangeFrozen"));
        Assert.Single(await NotificationsAsync(x.B, "ExchangeFrozen"));
        Assert.Equal(1, await AuditCountAsync(AuditActions.ExchangeFrozen, x.ExchangeId, x.Moderator.Id));
        var twice = await SendAsync(x.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/freeze-exchange");
        Assert.Equal("exchange_not_active", await CodeAsync(twice));

        var released = await SendAsync(x.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/unfreeze-exchange");
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Equal(ExchangeStatus.Active, (await ExchangeRowAsync(x.ExchangeId)).Status);
        Assert.Equal(1, await AuditCountAsync(AuditActions.ExchangeUnfrozen, x.ExchangeId, x.Moderator.Id));
        var again = await SendAsync(x.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/unfreeze-exchange");
        Assert.Equal("exchange_not_frozen", await CodeAsync(again));
    }

    [Fact]
    public async Task Freezing_is_for_complaints_the_owner_and_cases_in_work_only()
    {
        var dispute = await TakenDisputeAsync();
        var x = await TakenAsync();
        var other = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        await ResolveAsync(x.Moderator, x.CaseId, "NoViolation");

        var forDispute = await SendAsync(dispute.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{dispute.CaseId}/freeze-exchange");
        var notOwner = await SendAsync(other.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/freeze-exchange");
        var resolved = await SendAsync(x.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/freeze-exchange");

        Assert.Equal("case_type_not_supported", await CodeAsync(forDispute));
        Assert.Equal(HttpStatusCode.NotFound, notOwner.StatusCode);
        Assert.Equal("case_not_in_work", await CodeAsync(resolved));
    }

    // ---------- закрытие через 7 дней ----------

    private async Task RunCloseAsync(Guid caseId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CaseJobs>().CloseIfDueAsync(caseId);
    }

    [Fact]
    public async Task Seven_days_after_the_decision_the_case_is_closed_by_the_job_and_not_before()
    {
        var x = await TakenAsync();
        await ResolveAsync(x.Moderator, x.CaseId, "NoViolation");

        await RunCloseAsync(x.CaseId);   // семи дней ещё нет
        Assert.Equal(CaseStatus.Resolved, (await CaseAsync(x.CaseId)).Status);

        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == x.CaseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ResolvedAt, Now.AddDays(-8)));
            return 0;
        });
        await RunCloseAsync(x.CaseId);
        await RunCloseAsync(x.CaseId);   // повтор безвреден

        var stored = await CaseAsync(x.CaseId);
        Assert.Equal(CaseStatus.Closed, stored.Status);
        Assert.NotNull(stored.ClosedAt);
        Assert.Equal(1, await AuditCountAsync(AuditActions.CaseClosed, x.CaseId));
    }

    // ---------- удержание данных при удалении аккаунта ----------

    [Fact]
    public async Task While_a_case_is_not_closed_the_personal_data_of_its_parties_are_held()
    {
        var x = await TakenAsync();
        var outsider = (await ModerationTestData.ExchangeAsync(_factory, _client)).A;
        using var scope = _factory.Services.CreateScope();
        var hold = scope.ServiceProvider.GetRequiredService<IErasureHold>();

        Assert.True(await hold.HasOpenCaseAsync(x.A.Id, CancellationToken.None));
        Assert.True(await hold.HasOpenCaseAsync(x.B.Id, CancellationToken.None));
        Assert.False(await hold.HasOpenCaseAsync(outsider.Id, CancellationToken.None));

        await ResolveAsync(x.Moderator, x.CaseId, "NoViolation");
        Assert.True(await hold.HasOpenCaseAsync(x.B.Id, CancellationToken.None));   // решено, но срок апелляции идёт

        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == x.CaseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ResolvedAt, Now.AddDays(-8)));
            return 0;
        });
        await RunCloseAsync(x.CaseId);
        Assert.False(await hold.HasOpenCaseAsync(x.B.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_withdrawn_case_does_not_hold_the_data()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var caseId = await FileAsync(x.A, x.ExchangeId);
        await SendAsync(x.A.Token, HttpMethod.Post, $"/cases/{caseId}/withdraw");
        using var scope = _factory.Services.CreateScope();

        Assert.False(await scope.ServiceProvider.GetRequiredService<IErasureHold>().HasOpenCaseAsync(x.A.Id, CancellationToken.None));
    }
}
