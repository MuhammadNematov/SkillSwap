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

/// <summary>Пользователи в панели админа: поиск, карточка, ручная блокировка (те же последствия, что у одобренного бана), разблокировка, снятие предупреждения.</summary>
[Collection(ApiCollection.Name)]
public class AdminUsersApiTests
{
    private const string Reason = "Подтверждённые жалобы на мошенничество в чате.";
    private const string Complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public AdminUsersApiTests(ApiFactory factory)
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

    private Task<StaffPerson> AdminAsync(UserRole role = UserRole.Admin) => ModerationTestData.StaffAsync(_factory, role);

    private Task<User> UserAsync(Guid id) => _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<int> AuditCountAsync(string action, Guid target, Guid actor) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == action && a.TargetId == target && a.ActorId == actor));

    private async Task<List<JsonElement>> NotificationsAsync(string token, string type)
    {
        var items = (await JsonAsync(await SendAsync(token, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return items.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    /// <summary>Обмен двух людей и жалоба одного на другого, взятая модератором (чтобы было обращение, на котором держится предупреждение).</summary>
    private async Task<(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid CaseId, StaffPerson Moderator, MatchTestWorld World)> CaseAsync()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var created = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = Complaint });
        var caseId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/take")).StatusCode);
        return (x.A, x.B, x.ExchangeId, caseId, moderator, x.World);
    }

    private async Task<Warning> WarnAsync(Guid userId, Guid caseId, Guid issuedBy)
    {
        var warning = Warning.Issue(userId, caseId, issuedBy, "Оскорбления в чате обмена.", Now);
        await _factory.QueryAsync(async db =>
        {
            db.Warnings.Add(warning);
            await db.SaveChangesAsync();
            return 0;
        });
        return warning;
    }

    // ======================= список и карточка =======================

    [Fact]
    public async Task The_list_searches_regular_users_by_name_or_email_and_filters_by_status()
    {
        var admin = await AdminAsync();
        var marker = "Зюзя" + Guid.NewGuid().ToString("N")[..8];
        var (found, _) = await StaffActor.PersonalAsync(_factory);
        var (blocked, _) = await StaffActor.PersonalAsync(_factory);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == found.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Name, marker + " Первый"));
            await db.Users.Where(u => u.Id == blocked.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Name, marker + " Второй").SetProperty(u => u.Status, UserStatus.Blocked));
            return 0;
        });

        var byName = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/users?search={marker.ToLowerInvariant()}"));
        var byEmail = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/users?search={Uri.EscapeDataString(found.Email!.ToUpperInvariant())}"));
        var onlyBlocked = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/users?search={marker}&status=Blocked"));
        var wildcard = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/users?search=%25_%25zz-no-such"));

        Assert.Equal(2, byName.GetProperty("totalCount").GetInt32());
        Assert.Equal(found.Id, Assert.Single(byEmail.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var one = Assert.Single(onlyBlocked.GetProperty("items").EnumerateArray());
        Assert.Equal(blocked.Id, one.GetProperty("id").GetGuid());
        Assert.Equal("Blocked", one.GetProperty("status").GetString());
        Assert.Equal(0, wildcard.GetProperty("totalCount").GetInt32());   // % и _ — обычные символы, а не шаблон
    }

    [Fact]
    public async Task The_list_never_shows_staff_and_does_not_leak_the_birth_date()
    {
        var admin = await AdminAsync();
        var staff = await AdminAsync(UserRole.Moderator);

        var list = await SendAsync(admin.Token, HttpMethod.Get, $"/admin/users?search={Uri.EscapeDataString(staff.User.Email!)}");
        var json = await list.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(json).RootElement.GetProperty("totalCount").GetInt32());
        Assert.DoesNotContain("birthDate", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(admin.Token, HttpMethod.Get, $"/admin/users/{staff.Id}")).StatusCode);
    }

    [Fact]
    public async Task Only_admins_and_superadmins_open_the_users_panel()
    {
        var (_, personal) = await StaffActor.PersonalAsync(_factory);
        var moderator = await AdminAsync(UserRole.Moderator);
        var superAdmin = await AdminAsync(UserRole.SuperAdmin);
        var target = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(personal, HttpMethod.Get, "/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Get, "/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Post, $"/admin/users/{target}/block", new { reason = Reason })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Post, $"/admin/warnings/{target}/revoke", new { reason = Reason })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(superAdmin.Token, HttpMethod.Get, "/admin/users")).StatusCode);
        Assert.Equal("invalid_paging", await CodeAsync(await SendAsync(superAdmin.Token, HttpMethod.Get, "/admin/users?pageSize=500")));
    }

    [Fact]
    public async Task The_card_shows_warnings_cases_ban_requests_and_the_block_history()
    {
        var s = await CaseAsync();
        var admin = await AdminAsync();
        var warning = await WarnAsync(s.B.Id, s.CaseId, s.Moderator.Id);
        var revoked = await WarnAsync(s.B.Id, s.CaseId, s.Moderator.Id);
        await _factory.QueryAsync(async db =>
        {
            var w = await db.Warnings.SingleAsync(x => x.Id == revoked.Id);
            w.Revoke(Now);
            db.BanRequests.Add(BanRequest.ByModerator(s.B.Id, s.CaseId, s.Moderator.Id, "Нужна блокировка по итогам обращения."));
            await db.SaveChangesAsync();
            return 0;
        });
        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{s.B.Id}/block", new { reason = Reason });
        _factory.Clock.Advance(TimeSpan.FromSeconds(1));   // часы тестов заморожены: без сдвига у двух событий одно время и порядок не определён
        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{s.B.Id}/unblock", new { reason = "Ошибочная блокировка, разобрались." });

        var card = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/users/{s.B.Id}"));

        var user = card.GetProperty("user");
        Assert.Equal(s.B.Id, user.GetProperty("id").GetGuid());
        Assert.Equal("Active", user.GetProperty("status").GetString());
        Assert.Equal(1, user.GetProperty("activeWarnings").GetInt32());
        Assert.Equal(1, user.GetProperty("openCases").GetInt32());
        var warnings = card.GetProperty("warnings").EnumerateArray().ToList();
        Assert.Equal(2, warnings.Count);
        Assert.True(warnings.Single(w => w.GetProperty("id").GetGuid() == warning.Id).GetProperty("isActive").GetBoolean());
        Assert.False(warnings.Single(w => w.GetProperty("id").GetGuid() == revoked.Id).GetProperty("isActive").GetBoolean());
        var caseRow = Assert.Single(card.GetProperty("cases").EnumerateArray());
        Assert.Equal("Against", caseRow.GetProperty("role").GetString());
        var ban = Assert.Single(card.GetProperty("banRequests").EnumerateArray());
        Assert.Equal("Approved", ban.GetProperty("status").GetString());   // ручная блокировка закрыла запрос
        var history = card.GetProperty("blockHistory").EnumerateArray().ToList();
        Assert.Equal(["UserUnblocked", "UserBlocked"], history.Select(h => h.GetProperty("action").GetString()));
        Assert.Equal("manual", history[1].GetProperty("source").GetString());
        Assert.Equal(Reason, history[1].GetProperty("reason").GetString());
        Assert.Equal(admin.Id, history[1].GetProperty("byId").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(admin.Token, HttpMethod.Get, $"/admin/users/{Guid.NewGuid()}")).StatusCode);
    }

    // ======================= блокировка =======================

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
    public async Task A_manual_block_has_the_same_consequences_as_an_approved_ban()
    {
        var s = await CaseAsync();
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

        var lessonId = await _factory.QueryAsync(async db =>
        {
            var lesson = LessonSession.Propose(s.ExchangeId, s.A.Id, s.A.Id, Now.AddDays(2), null, Now, Now.AddDays(30));
            db.Add(lesson);
            db.Set<RefreshToken>().Add(RefreshToken.Create(s.B.Id, Guid.NewGuid().ToString("N"), Now.AddDays(10), "test", null, Guid.NewGuid(), Now));
            await db.SaveChangesAsync();
            return lesson.Id;
        });
        var world = s.World;
        var incoming = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == incoming.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var requested = await SendAsync(incoming.Token, HttpMethod.Post, "/exchange-requests",
            new { toUserId = s.B.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        Assert.Equal(HttpStatusCode.Created, requested.StatusCode);
        var requestId = (await JsonAsync(requested)).GetProperty("id").GetGuid();

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{s.B.Id}/block", new { reason = Reason });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Blocked", (await JsonAsync(response)).GetProperty("user").GetProperty("status").GetString());
        Assert.Equal(UserStatus.Blocked, (await UserAsync(s.B.Id)).Status);
        var tokens = await _factory.QueryAsync(db => db.Set<RefreshToken>().AsNoTracking().Where(t => t.UserId == s.B.Id).ToListAsync());
        Assert.NotEmpty(tokens);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));                                   // сеансы отозваны
        Assert.True(banned.Aborted);                                                            // соединения отключены
        Assert.False(partner.Aborted);
        var exchange = await _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == s.ExchangeId));
        Assert.Equal(ExchangeStatus.Aborted, exchange.Status);
        Assert.Equal("account_blocked", exchange.AbortReason);
        var partnerView = await JsonAsync(await SendAsync(s.A.Token, HttpMethod.Get, $"/exchanges/{s.ExchangeId}"));
        Assert.Equal("partner_unavailable", partnerView.GetProperty("abortReason").GetString());   // партнёр видит нейтральную причину
        var note = Assert.Single(await NotificationsAsync(s.A.Token, "ExchangeAborted")).GetProperty("payload");
        Assert.Equal("partner_unavailable", note.GetProperty("reason").GetString());
        Assert.Equal(SessionStatus.Cancelled, (await _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(x => x.Id == lessonId))).Status);
        Assert.Equal(ExchangeRequestStatus.Expired, await _factory.QueryAsync(db => db.ExchangeRequests.AsNoTracking().Where(r => r.Id == requestId).Select(r => r.Status).SingleAsync()));
        Assert.Empty(await NotificationsAsync(incoming.Token, "RequestExpired"));               // отправителю причина не раскрывается
        var letter = Assert.Single(_factory.Mail.To(s.B.User.Email!), m => m.Subject.Contains("аккаунт заблокирован") || m.Subject.Contains("account has been blocked"));
        Assert.DoesNotContain("оскорбля", letter.Body);
        Assert.DoesNotContain(Reason, letter.Body);                                             // причину пользователь не видит
        Assert.Equal(1, await AuditCountAsync("UserBlocked", s.B.Id, admin.Id));
        var afterBlock = await SendAsync(s.B.Token, HttpMethod.Get, "/me/cases");
        Assert.Equal("account_not_active", await CodeAsync(afterBlock));
    }

    [Fact]
    public async Task A_manual_block_closes_the_pending_ban_requests_as_approved_and_tells_the_requester()
    {
        var s = await CaseAsync();
        var admin = await AdminAsync();
        var ban = BanRequest.ByModerator(s.B.Id, s.CaseId, s.Moderator.Id, "Прошу заблокировать за оскорбления.");
        await _factory.QueryAsync(async db =>
        {
            db.BanRequests.Add(ban);
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{s.B.Id}/block", new { reason = Reason })).StatusCode);

        var row = await _factory.QueryAsync(db => db.BanRequests.AsNoTracking().SingleAsync(b => b.Id == ban.Id));
        Assert.Equal(BanRequestStatus.Approved, row.Status);
        Assert.Equal(admin.Id, row.DecidedById);
        Assert.Equal(1, await AuditCountAsync("BanDecided", s.B.Id, admin.Id));
        var note = Assert.Single(await NotificationsAsync(s.Moderator.Token, "BanRequestDecided"));
        Assert.True(note.GetProperty("payload").GetProperty("approved").GetBoolean());
    }

    [Fact]
    public async Task A_block_is_refused_for_staff_for_oneself_for_an_already_blocked_user_and_without_a_proper_reason()
    {
        var admin = await AdminAsync();
        var staff = await AdminAsync(UserRole.Moderator);
        var (user, _) = await StaffActor.PersonalAsync(_factory);

        var self = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{admin.Id}/block", new { reason = Reason });
        var moderator = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{staff.Id}/block", new { reason = Reason });
        var unknown = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{Guid.NewGuid()}/block", new { reason = Reason });
        var tooShort = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{user.Id}/block", new { reason = "мало" });
        var missing = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{user.Id}/block", new { });
        var tooLong = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{user.Id}/block", new { reason = new string('я', 501) });

        Assert.Equal("cannot_change_own_rights", await CodeAsync(self));
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("staff_cannot_be_blocked", await CodeAsync(moderator));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("block_reason_too_short", await CodeAsync(tooShort));
        Assert.Equal("block_reason_too_short", await CodeAsync(missing));
        Assert.Equal("block_reason_too_long", await CodeAsync(tooLong));
        Assert.Equal(UserStatus.Active, (await UserAsync(user.Id)).Status);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{user.Id}/block", new { reason = Reason })).StatusCode);
        var again = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{user.Id}/block", new { reason = Reason });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("user_not_active", await CodeAsync(again));
        Assert.Equal(1, await AuditCountAsync("UserBlocked", user.Id, admin.Id));
    }

    // ======================= разблокировка =======================

    [Theory]
    [InlineData("ru", "аккаунт разблокирован", "Войдите в него заново")]
    [InlineData("tg", "аккаунт боз карда шуд", "Бори дигар ворид шавед")]
    [InlineData("en", "your account has been unblocked", "Please sign in again")]
    public async Task Unblocking_restores_the_account_sends_a_letter_and_does_not_bring_back_the_exchanges(string language, string subject, string phrase)
    {
        var s = await CaseAsync();
        var admin = await AdminAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == s.B.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.UiLanguage, language));
            return 0;
        });
        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{s.B.Id}/block", new { reason = Reason });

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{s.B.Id}/unblock", new { reason = "Блокировка была ошибочной." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var card = await JsonAsync(response);
        Assert.Equal("Active", card.GetProperty("user").GetProperty("status").GetString());
        Assert.Equal(0, card.GetProperty("activeExchanges").GetInt32());
        Assert.Equal(UserStatus.Active, (await UserAsync(s.B.Id)).Status);
        var exchange = await _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == s.ExchangeId));
        Assert.Equal(ExchangeStatus.Aborted, exchange.Status);   // прерванный обмен не возвращается
        var letter = Assert.Single(_factory.Mail.To(s.B.User.Email!), m => m.Subject.Contains(subject));
        Assert.Contains(phrase, letter.Body);
        Assert.Contains(s.B.User.Name, letter.Body);
        Assert.DoesNotContain("Блокировка была ошибочной", letter.Body);
        Assert.Equal(1, await AuditCountAsync("UserUnblocked", s.B.Id, admin.Id));
    }

    [Fact]
    public async Task Unblocking_needs_a_blocked_user_and_a_reason()
    {
        var admin = await AdminAsync();
        var (user, _) = await StaffActor.PersonalAsync(_factory);

        var active = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{user.Id}/unblock", new { reason = Reason });
        var noReason = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{user.Id}/unblock", new { reason = " " });
        var unknown = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{Guid.NewGuid()}/unblock", new { reason = Reason });

        Assert.Equal("user_not_blocked", await CodeAsync(active));
        Assert.Equal("block_reason_too_short", await CodeAsync(noReason));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(0, await AuditCountAsync("UserUnblocked", user.Id, admin.Id));
    }

    // ======================= снятие предупреждения =======================

    [Fact]
    public async Task Revoking_a_warning_by_hand_tells_the_user_and_cannot_be_repeated()
    {
        var s = await CaseAsync();
        var admin = await AdminAsync();
        var warning = await WarnAsync(s.B.Id, s.CaseId, s.Moderator.Id);

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/warnings/{warning.Id}/revoke", new { reason = "Предупреждение выдано ошибочно." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var card = await JsonAsync(response);
        Assert.Equal(0, card.GetProperty("user").GetProperty("activeWarnings").GetInt32());
        Assert.False(Assert.Single(card.GetProperty("warnings").EnumerateArray()).GetProperty("isActive").GetBoolean());
        var note = Assert.Single(await NotificationsAsync(s.B.Token, "WarningRevoked")).GetProperty("payload");
        Assert.True(note.GetProperty("manual").GetBoolean());
        Assert.Equal(s.CaseId, note.GetProperty("caseId").GetGuid());
        Assert.Equal(1, await AuditCountAsync("WarningRevokedManually", s.B.Id, admin.Id));

        var again = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/warnings/{warning.Id}/revoke", new { reason = "Предупреждение выдано ошибочно." });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("warning_not_active", await CodeAsync(again));
        Assert.Equal("warning_not_found", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Post, $"/admin/warnings/{Guid.NewGuid()}/revoke", new { reason = Reason })));
        Assert.Equal("warning_revoke_reason_too_short", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Post, $"/admin/warnings/{warning.Id}/revoke", new { reason = "x" })));
    }

    [Fact]
    public async Task Revoking_a_warning_rejects_the_automatic_ban_request_when_fewer_than_three_remain_but_keeps_a_moderators_request()
    {
        var s = await CaseAsync();
        var admin = await AdminAsync();
        var first = await WarnAsync(s.B.Id, s.CaseId, s.Moderator.Id);
        await WarnAsync(s.B.Id, s.CaseId, s.Moderator.Id);
        await WarnAsync(s.B.Id, s.CaseId, s.Moderator.Id);
        var automatic = BanRequest.Automatic(s.B.Id, s.CaseId, "Три действующих предупреждения.");
        var byModerator = BanRequest.ByModerator(s.B.Id, s.CaseId, s.Moderator.Id, "Прошу заблокировать по итогам обращения.");
        await _factory.QueryAsync(async db =>
        {
            db.BanRequests.AddRange(automatic, byModerator);
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/warnings/{first.Id}/revoke", new { reason = "Предупреждение выдано ошибочно." })).StatusCode);

        var rows = await _factory.QueryAsync(db => db.BanRequests.AsNoTracking().Where(b => b.UserId == s.B.Id).ToDictionaryAsync(b => b.Id, b => b.Status));
        Assert.Equal(BanRequestStatus.Rejected, rows[automatic.Id]);
        Assert.Equal(BanRequestStatus.Pending, rows[byModerator.Id]);   // запрос модератора решает админ сам
        Assert.Equal(1, await AuditCountAsync("BanDecided", s.B.Id, admin.Id));
    }
}
