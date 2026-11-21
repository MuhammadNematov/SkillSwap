using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Staff;

/// <summary>Приглашения служебных аккаунтов (раздел 3 ТЗ): создание, список, отзыв, повторная отправка, приём по ссылке, подтверждение паролем и кодом 2FA, аудит и письма.</summary>
[Collection(ApiCollection.Name)]
public partial class StaffInvitationsApiTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public StaffInvitationsApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Служебный аккаунт с паролем и 2FA. Часы тестов заморожены, поэтому кодов приложения хватает на два действия; остальные подтверждаются резервными кодами
    /// (<see cref="Confirm"/>), каждый из которых одноразовый.
    /// </summary>
    private sealed class Actor
    {
        public required User User { get; init; }
        public required string Token { get; init; }
        public required string Key { get; init; }
        public required IReadOnlyList<string> Recovery { get; init; }
        public int Next;
    }

    private async Task<Actor> ActorAsync(UserRole role)
    {
        var user = User.CreateStaff(AuthApi.NewEmail(), role + " (invitations)", role);
        user.EmailConfirmed = true;
        using (var scope = _factory.Services.CreateScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, AuthApi.Password)).Succeeded);
        }

        var token = TestTokens.ForUser(_factory, user);
        var (key, codes) = await new AuthApi(_factory, TestIps.Next()).EnableTwoFactorAsync(token);
        return new Actor { User = user, Token = token, Key = key, Recovery = codes };
    }

    private static object Confirm(Actor actor) => new { password = AuthApi.Password, code = actor.Recovery[actor.Next++], isRecoveryCode = true };

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

    private Task<HttpResponseMessage> InviteAsync(Actor actor, string email, string role = "Moderator", string? language = null, object? confirmation = null) =>
        SendAsync(actor.Token, HttpMethod.Post, "/admin/staff-invitations", new { email, role, language, confirmation = confirmation ?? Confirm(actor) });

    private static string NewEmail() => $"invitee-{Guid.NewGuid():N}@example.com";

    /// <summary>Токен из письма с приглашением на этот адрес (последнее письмо).</summary>
    private string TokenFor(string email)
    {
        var mail = _factory.Mail.To(email).Last();
        var match = TokenInLink().Match(mail.Body);
        Assert.True(match.Success, mail.Body);
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    [GeneratedRegex(@"https://frontend\.test/staff/invitation/(\S+)")]
    private static partial Regex TokenInLink();

    private async Task<(Guid Id, string Email, string Token)> InvitedAsync(Actor actor, string role = "Moderator")
    {
        var email = NewEmail();
        var response = await InviteAsync(actor, email, role);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return ((await JsonAsync(response)).GetProperty("id").GetGuid(), email, TokenFor(email));
    }

    private Task<StaffInvitation> StoredAsync(Guid id) => _factory.QueryAsync(db => db.StaffInvitations.AsNoTracking().SingleAsync(i => i.Id == id));

    private Task<int> AuditCountAsync(string action, Guid? target = null) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == action && (target == null || a.TargetId == target)));

    private Task<HttpResponseMessage> AcceptAsync(string token, string name = "Новый модератор", string password = "Strong-pass-1", string? language = "ru") =>
        SendAsync(null, HttpMethod.Post, "/staff-invitations/accept", new { token, name, password, uiLanguage = language });

    // ======================= создание =======================

    [Fact]
    public async Task An_admin_invites_a_moderator_and_the_link_goes_only_by_email()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var email = NewEmail();

        var response = await InviteAsync(admin, email.ToUpperInvariant(), language: "ru");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var dto = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(email, dto.GetProperty("email").GetString());   // адрес нормализован
        Assert.Equal("Moderator", dto.GetProperty("targetRole").GetString());
        Assert.Equal("Pending", dto.GetProperty("status").GetString());
        Assert.Equal(admin.User.Id, dto.GetProperty("invitedBy").GetProperty("id").GetGuid());
        Assert.Equal(Now.AddHours(72), dto.GetProperty("expiresAt").GetDateTime(), TimeSpan.FromSeconds(10));

        var token = TokenFor(email);
        Assert.DoesNotContain(token, raw);   // токена в ответе нет
        var stored = await StoredAsync(dto.GetProperty("id").GetGuid());
        Assert.Equal(StaffInvitationTokens.Hash(token), stored.TokenHash);   // в базе только хеш
        Assert.DoesNotContain(token, stored.TokenHash);
        var mail = Assert.Single(_factory.Mail.To(email));
        Assert.Contains("приглашение", mail.Subject);
        Assert.Contains("модератор", mail.Body);
        Assert.Contains(admin.User.Name, mail.Body);
        Assert.Equal(1, await AuditCountAsync("StaffInvited", stored.Id));
    }

    [Theory]
    [InlineData("tg", "даъват")]
    [InlineData("ru", "приглашение")]
    [InlineData("en", "invitation")]
    [InlineData(null, "invitation")]      // язык пригласившего по умолчанию (en)
    [InlineData("de", "invitation")]
    public async Task The_letter_goes_out_in_the_requested_language_or_the_inviters(string? language, string expected)
    {
        var admin = await ActorAsync(UserRole.Admin);
        var email = NewEmail();

        await InviteAsync(admin, email, language: language);

        Assert.Contains(expected, Assert.Single(_factory.Mail.To(email)).Subject);
    }

    [Fact]
    public async Task Only_a_superadmin_invites_admins_and_nobody_invites_a_superadmin()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var superAdmin = await ActorAsync(UserRole.SuperAdmin);

        var byAdmin = await InviteAsync(admin, NewEmail(), "Admin");
        Assert.Equal(HttpStatusCode.Forbidden, byAdmin.StatusCode);
        Assert.Equal("staff_role_not_allowed", await CodeAsync(byAdmin));
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(superAdmin, NewEmail(), "Admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(superAdmin, NewEmail(), "Moderator")).StatusCode);
        foreach (var role in new[] { "SuperAdmin", "User" })
        {
            var response = await InviteAsync(superAdmin, NewEmail(), role);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("staff_role_not_allowed", await CodeAsync(response));
        }
    }

    [Fact]
    public async Task Moderators_and_users_cannot_use_the_invitation_routes_at_all()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var (id, _, _) = await InvitedAsync(admin);
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/admin/staff-invitations")).StatusCode);
        foreach (var token in new[] { moderator.Token, x.A.Token })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Get, "/admin/staff-invitations")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Post, "/admin/staff-invitations", new { email = NewEmail(), role = "Moderator" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Post, $"/admin/staff-invitations/{id}/revoke", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Post, $"/admin/staff-invitations/{id}/resend", new { })).StatusCode);
        }
    }

    [Fact]
    public async Task Without_a_valid_confirmation_nothing_is_created_or_sent()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var email = NewEmail();

        var none = await SendAsync(admin.Token, HttpMethod.Post, "/admin/staff-invitations", new { email, role = "Moderator" });
        var wrongPassword = await InviteAsync(admin, email, confirmation: new { password = "wrong-password-1", code = admin.Recovery[9], isRecoveryCode = true });
        var wrongCode = await InviteAsync(admin, email, confirmation: new { password = AuthApi.Password, code = "000000" });
        var noCode = await InviteAsync(admin, email, confirmation: new { password = AuthApi.Password });

        Assert.Equal("step_up_required", await CodeAsync(none));
        Assert.Equal("invalid_password", await CodeAsync(wrongPassword));
        Assert.Equal("invalid_two_factor_code", await CodeAsync(wrongCode));
        Assert.Equal("two_factor_code_required", await CodeAsync(noCode));
        Assert.Empty(_factory.Mail.To(email));
        Assert.Equal(0, await _factory.QueryAsync(db => db.StaffInvitations.CountAsync(i => i.Email == email)));
    }

    [Fact]
    public async Task A_recovery_code_confirms_once_only()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var code = admin.Recovery[0];
        var body = new { password = AuthApi.Password, code, isRecoveryCode = true };

        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(admin, NewEmail(), confirmation: body)).StatusCode);
        var again = await InviteAsync(admin, NewEmail(), confirmation: body);

        Assert.Equal("invalid_two_factor_code", await CodeAsync(again));
    }

    [Fact]
    public async Task A_bad_address_a_taken_address_and_a_second_open_invitation_are_refused()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (_, email, _) = await InvitedAsync(admin);

        var bad = await InviteAsync(admin, "not-an-address");
        var taken = await InviteAsync(admin, admin.User.Email!);
        var open = await InviteAsync(admin, email);

        Assert.Equal("invitation_email_invalid", await CodeAsync(bad));
        Assert.Equal("email_already_registered", await CodeAsync(taken));
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
        Assert.Equal("invitation_already_exists", await CodeAsync(open));
        Assert.Single(_factory.Mail.To(email));
    }

    [Fact]
    public async Task After_a_revocation_the_same_address_can_be_invited_again()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (id, email, _) = await InvitedAsync(admin);
        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{id}/revoke", new { confirmation = Confirm(admin) });

        var again = await InviteAsync(admin, email);

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    // ======================= список =======================

    [Fact]
    public async Task An_admin_sees_moderator_invitations_and_a_superadmin_sees_all_with_filters_and_paging()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var superAdmin = await ActorAsync(UserRole.SuperAdmin);
        var (moderatorId, _, _) = await InvitedAsync(superAdmin, "Moderator");
        var (adminId, _, _) = await InvitedAsync(superAdmin, "Admin");
        var (revokedId, _, _) = await InvitedAsync(admin);
        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{revokedId}/revoke", new { confirmation = Confirm(admin) });

        var forAdmin = (await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff-invitations?pageSize=50"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        var forSuper = (await JsonAsync(await SendAsync(superAdmin.Token, HttpMethod.Get, "/admin/staff-invitations?pageSize=50"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        var pending = (await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff-invitations?status=Pending&pageSize=50"))).GetProperty("items").EnumerateArray().ToList();
        var revoked = (await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff-invitations?status=Revoked&pageSize=50"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.Contains(moderatorId, forAdmin);
        Assert.DoesNotContain(adminId, forAdmin);   // приглашения админов админ не видит
        Assert.Contains(adminId, forSuper);
        Assert.Contains(moderatorId, forSuper);
        Assert.All(pending, i => Assert.Equal("Pending", i.GetProperty("status").GetString()));
        Assert.Contains(revokedId, revoked);
        Assert.DoesNotContain(moderatorId, revoked);
        Assert.Equal("invalid_paging", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff-invitations?page=0")));
    }

    [Fact]
    public async Task An_expired_invitation_is_listed_as_expired()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (id, _, _) = await InvitedAsync(admin);
        await _factory.QueryAsync(async db =>
        {
            await db.StaffInvitations.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, Now.AddHours(-1)));
            return 0;
        });

        var expired = (await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff-invitations?status=Expired&pageSize=50"))).GetProperty("items").EnumerateArray().ToList();

        Assert.Contains(expired, i => i.GetProperty("id").GetGuid() == id);
        Assert.DoesNotContain((await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/staff-invitations?status=Pending&pageSize=50"))).GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == id);
    }

    // ======================= отзыв и повторная отправка =======================

    [Fact]
    public async Task Revoking_kills_the_link_audits_once_and_a_repeat_changes_nothing()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (id, _, token) = await InvitedAsync(admin);

        var first = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{id}/revoke", new { confirmation = Confirm(admin) });
        var second = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{id}/revoke", new { confirmation = Confirm(admin) });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("Revoked", (await JsonAsync(first)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await AuditCountAsync("StaffInvitationRevoked", id));
        Assert.Equal("invitation_revoked", await CodeAsync(await SendAsync(null, HttpMethod.Get, $"/staff-invitations/{Uri.EscapeDataString(token)}")));
        Assert.Equal("invitation_revoked", await CodeAsync(await AcceptAsync(token)));
    }

    [Fact]
    public async Task Revoking_and_resending_need_the_confirmation_the_right_role_and_an_existing_invitation()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var superAdmin = await ActorAsync(UserRole.SuperAdmin);
        var (moderatorInvitation, _, _) = await InvitedAsync(admin);
        var (adminInvitation, _, _) = await InvitedAsync(superAdmin, "Admin");

        Assert.Equal("step_up_required", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{moderatorInvitation}/revoke", new { })));
        Assert.Equal("step_up_required", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{moderatorInvitation}/resend", new { })));
        Assert.Equal("staff_role_not_allowed", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{adminInvitation}/revoke", new { confirmation = Confirm(admin) })));
        Assert.Equal("staff_role_not_allowed", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{adminInvitation}/resend", new { confirmation = Confirm(admin) })));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{Guid.NewGuid()}/revoke", new { confirmation = Confirm(admin) })).StatusCode);
        Assert.Equal(StaffInvitationStatus.Pending, (await StoredAsync(moderatorInvitation)).StatusAt(Now));
        Assert.Equal(StaffInvitationStatus.Pending, (await StoredAsync(adminInvitation)).StatusAt(Now));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(superAdmin.Token, HttpMethod.Post, $"/admin/staff-invitations/{moderatorInvitation}/revoke", new { confirmation = Confirm(superAdmin) })).StatusCode);   // суперадмин может всё
    }

    [Fact]
    public async Task Resending_gives_a_new_link_and_a_new_term_and_the_old_link_stops_working()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (id, email, oldToken) = await InvitedAsync(admin);
        await _factory.QueryAsync(async db =>
        {
            await db.StaffInvitations.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, Now.AddHours(-5)));   // истекло
            return 0;
        });

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{id}/resend", new { confirmation = Confirm(admin) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("Pending", dto.GetProperty("status").GetString());
        Assert.Equal(1, dto.GetProperty("resendCount").GetInt32());
        Assert.Equal(Now.AddHours(72), dto.GetProperty("expiresAt").GetDateTime(), TimeSpan.FromSeconds(10));
        Assert.Equal(2, _factory.Mail.To(email).Count);
        var newToken = TokenFor(email);
        Assert.NotEqual(oldToken, newToken);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(null, HttpMethod.Get, $"/staff-invitations/{Uri.EscapeDataString(oldToken)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(null, HttpMethod.Get, $"/staff-invitations/{Uri.EscapeDataString(newToken)}")).StatusCode);
        Assert.Equal(1, await AuditCountAsync("StaffInvitationResent", id));
    }

    [Fact]
    public async Task A_resent_letter_goes_out_in_the_requested_language()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (id, email, _) = await InvitedAsync(admin);

        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{id}/resend", new { confirmation = Confirm(admin), language = "tg" });

        Assert.Contains("даъват", _factory.Mail.To(email).Last().Subject);
    }

    [Fact]
    public async Task An_accepted_or_revoked_invitation_cannot_be_resent_and_an_accepted_one_cannot_be_revoked()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (acceptedId, _, acceptedToken) = await InvitedAsync(admin);
        var (revokedId, _, _) = await InvitedAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(acceptedToken)).StatusCode);
        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{revokedId}/revoke", new { confirmation = Confirm(admin) });

        var resendAccepted = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{acceptedId}/resend", new { confirmation = Confirm(admin) });
        var resendRevoked = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{revokedId}/resend", new { confirmation = Confirm(admin) });
        var revokeAccepted = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff-invitations/{acceptedId}/revoke", new { confirmation = Confirm(admin) });

        foreach (var response in new[] { resendAccepted, resendRevoked, revokeAccepted })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("invalid_state_transition", await CodeAsync(response));
        }
    }

    // ======================= приём =======================

    [Fact]
    public async Task The_preview_shows_what_the_link_is_for_without_a_sign_in()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (_, email, token) = await InvitedAsync(admin);

        var response = await SendAsync(null, HttpMethod.Get, $"/staff-invitations/{Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal(email, dto.GetProperty("email").GetString());
        Assert.Equal("Moderator", dto.GetProperty("targetRole").GetString());
        Assert.Equal(admin.User.Name, dto.GetProperty("invitedByName").GetString());
    }

    [Theory]
    [InlineData("unknown-token")]
    [InlineData("a")]
    public async Task An_unknown_link_is_not_found(string token)
    {
        var preview = await SendAsync(null, HttpMethod.Get, $"/staff-invitations/{token}");
        var accept = await AcceptAsync(token);

        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Equal("invitation_not_found", await CodeAsync(preview));
        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
    }

    [Fact]
    public async Task Accepting_creates_a_separate_staff_account_with_the_invited_role_and_leads_to_the_two_factor_setup()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (id, email, token) = await InvitedAsync(admin);

        var response = await AcceptAsync(token, name: "Новый Модератор", language: "tg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.True(body.GetProperty("requires2faSetup").GetBoolean());
        var challenge = body.GetProperty("twoFactorToken").GetString()!;
        var user = await _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Email == email));
        Assert.Equal(UserRole.Moderator, user.Role);
        Assert.Equal(AccountType.Staff, user.AccountType);
        Assert.True(user.EmailConfirmed);
        Assert.Equal("Новый Модератор", user.Name);
        Assert.Equal("tg", user.UiLanguage);
        Assert.False(user.TwoFactorEnabled);
        var stored = await StoredAsync(id);
        Assert.Equal(StaffInvitationStatus.Accepted, stored.StatusAt(Now));
        Assert.Equal(user.Id, stored.AcceptedUserId);
        Assert.Equal(user.Id, (await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "StaffInvitationAccepted" && a.TargetId == id))).ActorId);
        var note = (await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items").EnumerateArray()
            .Single(n => n.GetProperty("type").GetString() == "StaffInvitationAccepted" && n.GetProperty("payload").GetProperty("userId").GetGuid() == user.Id);
        Assert.Equal("Moderator", note.GetProperty("payload").GetProperty("role").GetString());

        // Дальше обычная настройка 2FA служебного по токену настройки; после неё вход требует код.
        var api = new AuthApi(_factory, TestIps.Next());
        var setup = await api.TwoFactorSetupAsync(null, challenge);
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await JsonAsync(setup)).GetProperty("sharedKey").GetString()!;
        var verify = await api.TwoFactorVerifyAsync(null, api.TotpCode(key, -1), challenge);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        var login = await JsonAsync(await api.LoginAsync(email, "Strong-pass-1"));
        Assert.True(login.GetProperty("requires2fa").GetBoolean());
    }

    [Fact]
    public async Task An_invitation_for_an_admin_makes_an_admin()
    {
        var superAdmin = await ActorAsync(UserRole.SuperAdmin);
        var (_, email, token) = await InvitedAsync(superAdmin, "Admin");

        await AcceptAsync(token, name: "Новый админ");

        Assert.Equal(UserRole.Admin, (await _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Email == email))).Role);
    }

    [Fact]
    public async Task A_link_works_once_even_for_two_simultaneous_attempts()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (id, email, token) = await InvitedAsync(admin);

        var results = await Task.WhenAll(AcceptAsync(token), AcceptAsync(token));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        var loser = results.Single(r => r.StatusCode != HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal(1, await _factory.QueryAsync(db => db.Users.CountAsync(u => u.Email == email)));
        Assert.Equal(1, await AuditCountAsync("StaffInvitationAccepted", id));
        Assert.Equal("invitation_already_used", await CodeAsync(await AcceptAsync(token)));
    }

    [Fact]
    public async Task An_expired_link_is_refused_until_it_is_resent()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (id, email, token) = await InvitedAsync(admin);
        await _factory.QueryAsync(async db =>
        {
            await db.StaffInvitations.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAt, Now.AddMinutes(-1)));
            return 0;
        });

        Assert.Equal("invitation_expired", await CodeAsync(await SendAsync(null, HttpMethod.Get, $"/staff-invitations/{Uri.EscapeDataString(token)}")));
        Assert.Equal("invitation_expired", await CodeAsync(await AcceptAsync(token)));
        Assert.Equal(0, await _factory.QueryAsync(db => db.Users.CountAsync(u => u.Email == email)));
    }

    [Theory]
    [InlineData("", "Strong-pass-1", "invalid_name")]
    [InlineData("   ", "Strong-pass-1", "invalid_name")]
    [InlineData("Имя", "", "password_required")]
    [InlineData("Имя", "short", "weak_password")]
    public async Task Bad_input_is_refused_and_the_link_stays_valid(string name, string password, string code)
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (id, email, token) = await InvitedAsync(admin);

        var response = await AcceptAsync(token, name, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
        Assert.Equal(StaffInvitationStatus.Pending, (await StoredAsync(id)).StatusAt(Now));
        Assert.Equal(0, await _factory.QueryAsync(db => db.Users.CountAsync(u => u.Email == email)));
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(token)).StatusCode);   // исправил и принял
    }

    [Fact]
    public async Task If_the_address_got_taken_meanwhile_accepting_fails_cleanly()
    {
        var admin = await ActorAsync(UserRole.Admin);
        var (id, email, token) = await InvitedAsync(admin);
        var squatter = User.CreateRegular(email, "Занял адрес", new DateOnly(1995, 1, 1), "ru");
        using (var scope = _factory.Services.CreateScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(squatter, AuthApi.Password)).Succeeded);
        }

        var response = await AcceptAsync(token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("email_already_registered", await CodeAsync(response));
        Assert.Equal(StaffInvitationStatus.Pending, (await StoredAsync(id)).StatusAt(Now));
    }
}
