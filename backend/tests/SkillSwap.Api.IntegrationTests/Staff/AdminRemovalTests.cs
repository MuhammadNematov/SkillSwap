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
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Staff;

/// <summary>Снятие админа суперадмином (раздел 3 ТЗ, правило 4): последствия как у модератора, апелляции админа возвращаются в общую очередь.</summary>
[Collection(ApiCollection.Name)]
public class AdminRemovalTests
{
    private const string Complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";
    private const string Reason = "Админ покинул команду SkillSwap, доступ закрываем.";
    private const string AppealReason = "Не согласен с решением: переписка вырвана из контекста, прошу перепроверить.";
    private const string Decision = "Переписка подтверждает жалобу: оскорбления были, договорённости сорваны.";
    private const string Conclusion = "Переписку изучил: нарушение подтверждается, решение стоит оставить без изменений.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public AdminRemovalTests(ApiFactory factory)
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

    private Task<int> AuditCountAsync(string action, Guid target, Guid actor) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == action && a.TargetId == target && a.ActorId == actor));

    private Task<User> UserAsync(Guid id) => _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<CaseAppeal> AppealAsync(Guid caseId) => _factory.QueryAsync(db => db.CaseAppeals.AsNoTracking().SingleAsync(a => a.CaseId == caseId));

    private Task<Case> CaseRowAsync(Guid id) => _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task<StaffActor> StaffAsync(UserRole role) => StaffActor.CreateAsync(_factory, role);

    private Task<HttpResponseMessage> RemoveAsync(StaffActor actor, Guid targetId, object? reason = null, object? confirmation = null) =>
        SendAsync(actor.Token, HttpMethod.Post, $"/admin/staff/admins/{targetId}/remove", new { reason = reason ?? Reason, confirmation = confirmation ?? actor.Confirm() });

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

    /// <summary>Апелляция на повторной проверке: её ведёт <paramref name="admin"/> (он назначил проверяющего).</summary>
    private async Task<(Guid CaseId, StaffPerson Reviewer)> UnderReviewAsync(StaffActor admin)
    {
        var author = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var reviewer = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var created = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = Complaint });
        var caseId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(author.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/take")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(author.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/resolve", new { decision = "Warning", alsoWarned = false, reason = Decision })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(x.B.Token, HttpMethod.Post, $"/cases/{caseId}/appeal", new { reason = AppealReason })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/cases/{caseId}/appeal/reviewer", new { moderatorId = reviewer.Id })).StatusCode);
        Assert.Equal(admin.Id, (await AppealAsync(caseId)).AdminId);
        return (caseId, reviewer);
    }

    [Fact]
    public async Task The_superadmin_removes_an_admin_and_the_sessions_and_connections_are_closed()
    {
        var superAdmin = await StaffAsync(UserRole.SuperAdmin);
        var admin = await StaffAsync(UserRole.Admin);
        var colleague = await StaffAsync(UserRole.Admin);
        await _factory.QueryAsync(async db =>
        {
            var now = _factory.Clock.GetUtcNow().UtcDateTime;
            db.Set<RefreshToken>().Add(RefreshToken.Create(admin.Id, Guid.NewGuid().ToString("N"), now.AddDays(10), "test", null, Guid.NewGuid(), now));
            await db.SaveChangesAsync();
            return 0;
        });
        var registry = _factory.Services.GetRequiredService<ConnectionRegistry>();
        var removed = new FakeHubContext(admin.Id);
        var other = new FakeHubContext(colleague.Id);
        using (var scope = _factory.Services.CreateScope())
        {
            var profiles = scope.ServiceProvider.GetRequiredService<SkillSwap.Application.Profile.IProfileStore>();
            Assert.True(await registry.AcceptAsync(removed, profiles));
            Assert.True(await registry.AcceptAsync(other, profiles));
        }

        var response = await RemoveAsync(superAdmin, admin.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await JsonAsync(response);
        Assert.Equal(admin.Id, result.GetProperty("id").GetGuid());
        Assert.True(result.GetProperty("removed").GetBoolean());
        Assert.Equal(0, result.GetProperty("appealsReturned").GetInt32());
        Assert.Equal(UserStatus.Blocked, (await UserAsync(admin.Id)).Status);
        Assert.Equal(UserRole.Admin, (await UserAsync(admin.Id)).Role);   // роль остаётся для истории
        var tokens = await _factory.QueryAsync(db => db.Set<RefreshToken>().AsNoTracking().Where(t => t.UserId == admin.Id).ToListAsync());
        Assert.NotEmpty(tokens);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        Assert.True(removed.Aborted);
        Assert.False(other.Aborted);
        Assert.Equal(1, await AuditCountAsync("StaffRemoved", admin.Id, superAdmin.Id));
        var details = JsonDocument.Parse((await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "StaffRemoved" && a.TargetId == admin.Id))).DetailsJson).RootElement;
        Assert.Equal(Reason, details.GetProperty("reason").GetString());
        Assert.Equal("Admin", details.GetProperty("role").GetString());
        // Снятый админ больше ничего не делает, даже с живым токеном.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(admin.Token, HttpMethod.Get, "/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff/moderators")).StatusCode);
    }

    [Fact]
    public async Task The_appeals_the_admin_led_go_back_to_the_common_queue_of_admins()
    {
        var superAdmin = await StaffAsync(UserRole.SuperAdmin);
        var admin = await StaffAsync(UserRole.Admin);
        var colleague = await StaffAsync(UserRole.Admin);
        var (underReview, reviewer) = await UnderReviewAsync(admin);
        var (waiting, reviewer2) = await UnderReviewAsync(admin);
        // Вторая апелляция уже с заключением: ждёт решения админа.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(reviewer2.Token, HttpMethod.Post, $"/moderation/cases/{waiting}/appeal-conclusion", new { conclusion = Conclusion })).StatusCode);
        Assert.Equal(CaseStatus.Appealed, (await CaseRowAsync(waiting)).Status);

        var response = await RemoveAsync(superAdmin, admin.Id);

        Assert.Equal(2, (await JsonAsync(response)).GetProperty("appealsReturned").GetInt32());
        Assert.Null((await AppealAsync(underReview)).AdminId);
        Assert.Null((await AppealAsync(waiting)).AdminId);
        Assert.Equal(CaseStatus.AppealReview, (await CaseRowAsync(underReview)).Status);   // проверка продолжается
        Assert.Equal(1, await AuditCountAsync("AppealAdminReleased", underReview, superAdmin.Id));
        // Оставшиеся админы узнают об апелляции, которая ждёт решения; та, что на проверке, объявится с заключением.
        var appealed = await NotificationsAsync(colleague.Token, "CaseAppealed");
        Assert.Equal(2, appealed.Count(n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == waiting));   // при подаче и при возврате в очередь
        Assert.Equal(1, appealed.Count(n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == underReview));   // только при подаче
        Assert.Equal(1, (await NotificationsAsync(superAdmin.Token, "CaseAppealed")).Count(n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == waiting));   // снимающему повтор не приходит
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(reviewer.Token, HttpMethod.Post, $"/moderation/cases/{underReview}/appeal-conclusion", new { conclusion = Conclusion })).StatusCode);
        Assert.Contains(await NotificationsAsync(colleague.Token, "AppealConclusionReady"), n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == underReview);
        Assert.Contains(await NotificationsAsync(superAdmin.Token, "AppealConclusionReady"), n => n.GetProperty("payload").GetProperty("caseId").GetGuid() == underReview);

        // Другой админ выносит окончательное решение.
        var decided = await SendAsync(colleague.Token, HttpMethod.Post, $"/admin/cases/{waiting}/appeal/decision", new { outcome = "Upheld" });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        Assert.Equal(CaseStatus.Closed, (await CaseRowAsync(waiting)).Status);
    }

    [Fact]
    public async Task A_decided_appeal_keeps_its_admin_in_the_history()
    {
        var superAdmin = await StaffAsync(UserRole.SuperAdmin);
        var admin = await StaffAsync(UserRole.Admin);
        var (caseId, reviewer) = await UnderReviewAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(reviewer.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/appeal-conclusion", new { conclusion = Conclusion })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/cases/{caseId}/appeal/decision", new { outcome = "Upheld" })).StatusCode);

        var response = await RemoveAsync(superAdmin, admin.Id);

        Assert.Equal(0, (await JsonAsync(response)).GetProperty("appealsReturned").GetInt32());
        Assert.Equal(admin.Id, (await AppealAsync(caseId)).AdminId);   // окончательное решение осталось за ним
    }

    [Fact]
    public async Task Only_a_superadmin_may_remove_an_admin_and_never_oneself_or_a_superadmin()
    {
        var superAdmin = await StaffAsync(UserRole.SuperAdmin);
        var otherSuper = await StaffAsync(UserRole.SuperAdmin);
        var admin = await StaffAsync(UserRole.Admin);
        var peer = await StaffAsync(UserRole.Admin);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var (regular, _) = await StaffActor.PersonalAsync(_factory);

        var byAdmin = await RemoveAsync(admin, peer.Id);
        var self = await RemoveAsync(superAdmin, superAdmin.Id);
        var selfAdmin = await RemoveAsync(admin, admin.Id);
        var superTarget = await RemoveAsync(superAdmin, otherSuper.Id);
        var viaModeratorRoute = await SendAsync(superAdmin.Token, HttpMethod.Post, $"/admin/staff/moderators/{admin.Id}/remove", new { reason = Reason, confirmation = superAdmin.Confirm() });
        var asModerator = await RemoveAsync(superAdmin, moderator.Id);
        var unknown = await RemoveAsync(superAdmin, Guid.NewGuid());
        var asRegular = await RemoveAsync(superAdmin, regular.Id);
        var tooShort = await RemoveAsync(superAdmin, admin.Id, reason: "мало");
        var noConfirmation = await SendAsync(superAdmin.Token, HttpMethod.Post, $"/admin/staff/admins/{admin.Id}/remove", new { reason = Reason });
        var wrongPassword = await RemoveAsync(superAdmin, admin.Id, confirmation: new { password = "Wrong-password-1", code = superAdmin.Recovery[superAdmin.Next++], isRecoveryCode = true });
        var asModeratorCaller = await SendAsync(moderator.Token, HttpMethod.Post, $"/admin/staff/admins/{admin.Id}/remove", new { reason = Reason });

        Assert.Equal(HttpStatusCode.Forbidden, byAdmin.StatusCode);
        Assert.Equal("staff_role_not_allowed", await CodeAsync(byAdmin));
        Assert.Equal("cannot_change_own_rights", await CodeAsync(self));
        Assert.Equal("cannot_change_own_rights", await CodeAsync(selfAdmin));
        Assert.Equal("staff_role_not_allowed", await CodeAsync(superTarget));
        Assert.Equal(HttpStatusCode.Conflict, viaModeratorRoute.StatusCode);   // админа через маршрут модераторов не снять
        Assert.Equal("not_a_moderator", await CodeAsync(viaModeratorRoute));
        Assert.Equal("not_an_admin", await CodeAsync(asModerator));
        Assert.Equal("admin_not_found", await CodeAsync(unknown));
        Assert.Equal("admin_not_found", await CodeAsync(asRegular));
        Assert.Equal("removal_reason_too_short", await CodeAsync(tooShort));
        Assert.Equal("step_up_required", await CodeAsync(noConfirmation));
        Assert.Equal("invalid_password", await CodeAsync(wrongPassword));
        Assert.Equal(HttpStatusCode.Forbidden, asModeratorCaller.StatusCode);
        foreach (var id in new[] { admin.Id, peer.Id, otherSuper.Id, superAdmin.Id, moderator.Id })
        {
            Assert.Equal(UserStatus.Active, (await UserAsync(id)).Status);
        }

        Assert.Equal(0, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == "StaffRemoved" && a.ActorId == superAdmin.Id)));

        Assert.Equal(HttpStatusCode.OK, (await RemoveAsync(superAdmin, admin.Id)).StatusCode);
        var again = await RemoveAsync(superAdmin, admin.Id);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("staff_already_removed", await CodeAsync(again));
    }
}
