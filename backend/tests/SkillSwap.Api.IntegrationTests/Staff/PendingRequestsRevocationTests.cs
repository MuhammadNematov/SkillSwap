using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Staff;

/// <summary>
/// Этап 7, шаг 9: когда аккаунт заблокирован или сотрудник снят, его неразобранные приглашения и запросы на привязку отзываются, ссылки из писем перестают работать,
/// а письма получателям не уходят. Чужие запросы и уже разобранные не трогаются.
/// </summary>
[Collection(ApiCollection.Name)]
public class PendingRequestsRevocationTests
{
    private const string Reason = "Нарушение правил сообщества, подтверждённое обращениями.";
    private const string RemovalReason = "Сотрудник покинул команду SkillSwap, доступ закрываем.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public PendingRequestsRevocationTests(ApiFactory factory)
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

    /// <summary>Запрос на привязку в базе с известным токеном (письмо не отправляется).</summary>
    private async Task<(Guid Id, string Token)> LinkRequestAsync(Guid staffId, Guid personalId)
    {
        var (token, hash) = StaffInvitationTokens.Generate();
        var request = PersonalLinkRequest.Create(staffId, personalId, hash, Now);
        await _factory.QueryAsync(async db =>
        {
            db.PersonalLinkRequests.Add(request);
            await db.SaveChangesAsync();
            return 0;
        });
        return (request.Id, token);
    }

    private async Task<(Guid Id, string Token, string Email)> InvitationAsync(Guid inviterId, UserRole role = UserRole.Moderator, bool expired = false)
    {
        var (token, hash) = StaffInvitationTokens.Generate();
        var email = $"invitee-{Guid.NewGuid():N}@example.com";
        var invitation = StaffInvitation.Create(email, role, inviterId, hash, expired ? Now.AddDays(-10) : Now);
        await _factory.QueryAsync(async db =>
        {
            db.StaffInvitations.Add(invitation);
            await db.SaveChangesAsync();
            return 0;
        });
        return (invitation.Id, token, email);
    }

    private Task<PersonalLinkRequest> LinkRowAsync(Guid id) => _factory.QueryAsync(db => db.PersonalLinkRequests.AsNoTracking().SingleAsync(r => r.Id == id));

    private Task<StaffInvitation> InvitationRowAsync(Guid id) => _factory.QueryAsync(db => db.StaffInvitations.AsNoTracking().SingleAsync(i => i.Id == id));

    private Task<int> AuditCountAsync(string action, Guid target, Guid actor) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == action && a.TargetId == target && a.ActorId == actor));

    private async Task<JsonElement> AuditDetailsAsync(string action, Guid target)
    {
        var row = await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == action && a.TargetId == target));
        return JsonDocument.Parse(row.DetailsJson).RootElement;
    }

    [Fact]
    public async Task Blocking_a_personal_account_cancels_the_link_requests_to_it_and_leaves_the_others()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var moderator = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var otherModerator = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (blocked, blockedToken) = await StaffActor.PersonalAsync(_factory);
        var (bystander, bystanderToken) = await StaffActor.PersonalAsync(_factory);
        var toBlocked = await LinkRequestAsync(moderator.Id, blocked.Id);
        var toBystander = await LinkRequestAsync(otherModerator.Id, bystander.Id);
        var mailBefore = _factory.Mail.To(blocked.Email!).Count;

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{blocked.Id}/block", new { reason = Reason });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await LinkRowAsync(toBlocked.Id)).CancelledAt);
        Assert.Null((await LinkRowAsync(toBystander.Id)).CancelledAt);
        Assert.NotEqual(HttpStatusCode.OK, (await SendAsync(blockedToken, HttpMethod.Get, $"/staff-link-requests/{toBlocked.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(bystanderToken, HttpMethod.Get, $"/staff-link-requests/{toBystander.Token}")).StatusCode);
        Assert.Equal(1, await AuditCountAsync("PersonalLinkRequestCancelled", toBlocked.Id, admin.Id));
        var details = await AuditDetailsAsync("UserBlocked", blocked.Id);
        Assert.Equal(1, details.GetProperty("linkRequestsCancelled").GetInt32());
        Assert.Equal(0, details.GetProperty("invitationsRevoked").GetInt32());
        // Письма о блокировке уходят, как и раньше (одно), но не письмо об отмене запроса и не письмо тому, кто просил привязку.
        Assert.Equal(1, _factory.Mail.To(blocked.Email!).Count - mailBefore);
        Assert.Empty(_factory.Mail.To(moderator.User.Email!).Where(m => m.Subject.Contains("привяз", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Removing_a_moderator_cancels_the_link_requests_they_sent_without_any_letters()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var moderator = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (personal, personalToken) = await StaffActor.PersonalAsync(_factory);
        var (other, _) = await StaffActor.PersonalAsync(_factory);
        var pending = await LinkRequestAsync(moderator.Id, personal.Id);
        var olderPending = await LinkRequestAsync(moderator.Id, other.Id);
        var mailBefore = _factory.Mail.To(personal.Email!).Count + _factory.Mail.To(other.Email!).Count;

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff/moderators/{moderator.Id}/remove", new { reason = RemovalReason, confirmation = admin.Confirm() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await LinkRowAsync(pending.Id)).CancelledAt);
        Assert.NotNull((await LinkRowAsync(olderPending.Id)).CancelledAt);
        Assert.NotEqual(HttpStatusCode.OK, (await SendAsync(personalToken, HttpMethod.Get, $"/staff-link-requests/{pending.Token}")).StatusCode);
        Assert.Equal(1, await AuditCountAsync("PersonalLinkRequestCancelled", pending.Id, admin.Id));
        var details = await AuditDetailsAsync("StaffRemoved", moderator.Id);
        Assert.Equal(2, details.GetProperty("linkRequestsCancelled").GetInt32());
        Assert.Equal(mailBefore, _factory.Mail.To(personal.Email!).Count + _factory.Mail.To(other.Email!).Count);   // получатели ничего не получили
    }

    [Fact]
    public async Task Removing_an_admin_revokes_their_pending_invitations_only_and_sends_no_letters()
    {
        var superAdmin = await StaffActor.CreateAsync(_factory, UserRole.SuperAdmin);
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var colleague = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var pending = await InvitationAsync(admin.Id);
        var pendingAdmin = await InvitationAsync(admin.Id, UserRole.Admin);
        var expired = await InvitationAsync(admin.Id, expired: true);
        var accepted = await InvitationAsync(admin.Id);
        var revokedEarlier = await InvitationAsync(admin.Id);
        var colleagues = await InvitationAsync(colleague.Id);
        var invitee = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        await _factory.QueryAsync(async db =>
        {
            (await db.StaffInvitations.SingleAsync(i => i.Id == accepted.Id)).Accept(invitee.Id, Now);
            (await db.StaffInvitations.SingleAsync(i => i.Id == revokedEarlier.Id)).Revoke(Now.AddMinutes(-5));
            await db.SaveChangesAsync();
            return 0;
        });
        var earlierRevokedAt = (await InvitationRowAsync(revokedEarlier.Id)).RevokedAt;
        var emails = new[] { pending.Email, pendingAdmin.Email, expired.Email, accepted.Email, colleagues.Email };
        var mailBefore = emails.Sum(e => _factory.Mail.To(e).Count);

        var response = await SendAsync(superAdmin.Token, HttpMethod.Post, $"/admin/staff/admins/{admin.Id}/remove", new { reason = RemovalReason, confirmation = superAdmin.Confirm() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await InvitationRowAsync(pending.Id)).RevokedAt);
        Assert.NotNull((await InvitationRowAsync(pendingAdmin.Id)).RevokedAt);
        Assert.Null((await InvitationRowAsync(expired.Id)).RevokedAt);          // истёкшее и так не работает, отзывать нечего
        Assert.NotNull((await InvitationRowAsync(accepted.Id)).AcceptedAt);
        Assert.Null((await InvitationRowAsync(accepted.Id)).RevokedAt);          // принятое не трогается
        Assert.Equal(earlierRevokedAt, (await InvitationRowAsync(revokedEarlier.Id)).RevokedAt);   // отозванное прежде не переписывается
        Assert.Null((await InvitationRowAsync(colleagues.Id)).RevokedAt);       // приглашение коллеги работает
        Assert.NotEqual(HttpStatusCode.OK, (await SendAsync(null, HttpMethod.Get, $"/staff-invitations/{pending.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(null, HttpMethod.Get, $"/staff-invitations/{colleagues.Token}")).StatusCode);
        Assert.Equal(1, await AuditCountAsync("StaffInvitationRevoked", pending.Id, superAdmin.Id));
        Assert.Equal(0, await AuditCountAsync("StaffInvitationRevoked", colleagues.Id, superAdmin.Id));
        var details = await AuditDetailsAsync("StaffRemoved", admin.Id);
        Assert.Equal(2, details.GetProperty("invitationsRevoked").GetInt32());
        Assert.Equal(mailBefore, emails.Sum(e => _factory.Mail.To(e).Count));
    }

    [Fact]
    public async Task A_removed_admins_invitation_cannot_be_accepted_any_more()
    {
        var superAdmin = await StaffActor.CreateAsync(_factory, UserRole.SuperAdmin);
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var invitation = await InvitationAsync(admin.Id);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(null, HttpMethod.Get, $"/staff-invitations/{invitation.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(superAdmin.Token, HttpMethod.Post, $"/admin/staff/admins/{admin.Id}/remove", new { reason = RemovalReason, confirmation = superAdmin.Confirm() })).StatusCode);

        var accept = await SendAsync(null, HttpMethod.Post, "/staff-invitations/accept", new { token = invitation.Token, name = "Новый модератор", password = "Strong-pass-1", uiLanguage = "ru" });

        Assert.False(accept.IsSuccessStatusCode);
        Assert.Null(await _factory.QueryAsync(db => db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == invitation.Email)));   // служебный аккаунт не создан
    }

    [Fact]
    public async Task A_removal_with_nothing_pending_writes_zero_counters_and_still_succeeds()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var moderator = await StaffActor.CreateAsync(_factory, UserRole.Moderator);

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff/moderators/{moderator.Id}/remove", new { reason = RemovalReason, confirmation = admin.Confirm() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var details = await AuditDetailsAsync("StaffRemoved", moderator.Id);
        Assert.Equal(0, details.GetProperty("invitationsRevoked").GetInt32());
        Assert.Equal(0, details.GetProperty("linkRequestsCancelled").GetInt32());
    }
}
