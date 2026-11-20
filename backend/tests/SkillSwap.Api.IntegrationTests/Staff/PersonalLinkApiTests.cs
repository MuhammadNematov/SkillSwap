using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Staff;

/// <summary>Привязка личного аккаунта к служебному (раздел 3 ТЗ, правило 3): запрос, письмо, подтверждение владельцем, отказ, отмена, снятие и влияние на конфликт интересов.</summary>
[Collection(ApiCollection.Name)]
public partial class PersonalLinkApiTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public PersonalLinkApiTests(ApiFactory factory)
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

    private Task<HttpResponseMessage> RequestAsync(StaffActor staff, string email, string? language = null, object? confirmation = null) =>
        SendAsync(staff.Token, HttpMethod.Post, "/me/staff-link/request", new { email, language, confirmation = confirmation ?? staff.Confirm() });

    [GeneratedRegex(@"https://frontend\.test/staff-link/(\S+)")]
    private static partial Regex LinkInMail();

    private string TokenFor(string email)
    {
        var mail = _factory.Mail.To(email).Last();
        var match = LinkInMail().Match(mail.Body);
        Assert.True(match.Success, mail.Body);
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    private Task<User> UserAsync(Guid id) => _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<int> AuditCountAsync(string action, Guid? target = null, Guid? actor = null) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == action && (target == null || a.TargetId == target) && (actor == null || a.ActorId == actor)));

    private async Task<List<JsonElement>> NotificationsAsync(string token, string type) =>
        (await JsonAsync(await SendAsync(token, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items").EnumerateArray()
        .Where(n => n.GetProperty("type").GetString() == type).ToList();

    /// <summary>Сотрудник просит привязку, письмо пришло: возвращает токен из письма.</summary>
    private async Task<string> RequestedAsync(StaffActor staff, User personal)
    {
        var response = await RequestAsync(staff, personal.Email!);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return TokenFor(personal.Email!);
    }

    // ======================= запрос =======================

    [Fact]
    public async Task A_staff_member_requests_a_link_and_the_letter_goes_to_the_personal_mailbox_only()
    {
        var staff = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (personal, _) = await StaffActor.PersonalAsync(_factory, language: "ru");

        var response = await RequestAsync(staff, personal.Email!.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var state = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(JsonValueKind.Null, state.GetProperty("linked").ValueKind);
        Assert.Equal(personal.Email, state.GetProperty("pending").GetProperty("personalEmail").GetString());
        Assert.Equal(Now.AddHours(24), state.GetProperty("pending").GetProperty("expiresAt").GetDateTime(), TimeSpan.FromSeconds(10));
        var token = TokenFor(personal.Email!);
        Assert.DoesNotContain(token, raw);   // токена в ответе нет
        var mail = Assert.Single(_factory.Mail.To(personal.Email!));
        Assert.Contains("привязать личный аккаунт", mail.Subject);   // язык личного аккаунта
        Assert.Contains(staff.User.Name, mail.Body);
        Assert.Empty(_factory.Mail.To(staff.User.Email!));
        Assert.Equal(1, await AuditCountAsync("PersonalLinkRequested", personal.Id, staff.Id));
        Assert.Null((await UserAsync(staff.Id)).LinkedUserId);   // пока ничего не привязано
    }

    [Theory]
    [InlineData("tg", "дархост")]
    [InlineData("en", "request to link")]
    [InlineData("de", "привязать")]   // неподдерживаемый язык: язык личного аккаунта
    public async Task The_letter_language_can_be_chosen(string language, string expected)
    {
        var staff = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var (personal, _) = await StaffActor.PersonalAsync(_factory, language: "ru");

        await RequestAsync(staff, personal.Email!, language);

        Assert.Contains(expected, Assert.Single(_factory.Mail.To(personal.Email!)).Subject);
    }

    [Fact]
    public async Task Without_a_valid_confirmation_nothing_is_sent()
    {
        var staff = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (personal, _) = await StaffActor.PersonalAsync(_factory);

        var none = await SendAsync(staff.Token, HttpMethod.Post, "/me/staff-link/request", new { email = personal.Email });
        var wrong = await RequestAsync(staff, personal.Email!, confirmation: new { password = "wrong-password-1", code = staff.Recovery[9], isRecoveryCode = true });

        Assert.Equal("step_up_required", await CodeAsync(none));
        Assert.Equal("invalid_password", await CodeAsync(wrong));
        Assert.Empty(_factory.Mail.To(personal.Email!));
    }

    [Fact]
    public async Task Only_a_real_active_confirmed_regular_account_can_be_linked()
    {
        var staff = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var other = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var (unconfirmed, _) = await StaffActor.PersonalAsync(_factory, emailConfirmed: false);
        var (blocked, _) = await StaffActor.PersonalAsync(_factory);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == blocked.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Blocked));
            return 0;
        });

        foreach (var email in new[] { "nobody@example.com", other.User.Email!, unconfirmed.Email!, blocked.Email!, "", "   " })
        {
            var response = await RequestAsync(staff, email);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("personal_account_not_found", await CodeAsync(response));
        }

        Assert.Empty(_factory.Mail.To(unconfirmed.Email!));
        Assert.Empty(_factory.Mail.To(blocked.Email!));
    }

    [Fact]
    public async Task Regular_users_and_anonymous_cannot_use_the_staff_side()
    {
        var (person, token) = await StaffActor.PersonalAsync(_factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/me/staff-link")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Get, "/me/staff-link")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Post, "/me/staff-link/request", new { email = person.Email })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Post, "/me/staff-link/unlink-request", new { reason = "Причина для снятия привязки." })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Post, "/me/staff-link/cancel")).StatusCode);
    }

    [Fact]
    public async Task No_more_than_five_requests_per_hour()
    {
        var staff = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        for (var i = 0; i < 5; i++)
        {
            var (person, _) = await StaffActor.PersonalAsync(_factory);
            Assert.Equal(HttpStatusCode.OK, (await RequestAsync(staff, person.Email!)).StatusCode);
        }

        var (extra, _) = await StaffActor.PersonalAsync(_factory);
        var sixth = await RequestAsync(staff, extra.Email!);

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        Assert.Equal("link_request_rate_limited", await CodeAsync(sixth));
        Assert.Empty(_factory.Mail.To(extra.Email!));
    }

    // ======================= подтверждение владельцем =======================

    [Fact]
    public async Task The_owner_sees_the_request_and_confirming_links_the_accounts()
    {
        var staff = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (personal, personalToken) = await StaffActor.PersonalAsync(_factory);
        var token = await RequestedAsync(staff, personal);

        var preview = await SendAsync(personalToken, HttpMethod.Get, $"/staff-link-requests/{Uri.EscapeDataString(token)}");
        var confirm = await SendAsync(personalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(token)}/confirm");

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var dto = await JsonAsync(preview);
        Assert.Equal(staff.User.Name, dto.GetProperty("staffName").GetString());
        Assert.Equal("Moderator", dto.GetProperty("staffRole").GetString());
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        Assert.Equal(personal.Id, (await UserAsync(staff.Id)).LinkedUserId);
        var state = await JsonAsync(await SendAsync(staff.Token, HttpMethod.Get, "/me/staff-link"));
        Assert.Equal(personal.Id, state.GetProperty("linked").GetProperty("userId").GetGuid());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("pending").ValueKind);
        var note = Assert.Single(await NotificationsAsync(staff.Token, "PersonalLinkConfirmed")).GetProperty("payload");
        Assert.Equal(personal.Id, note.GetProperty("userId").GetGuid());
        Assert.Equal(0, note.GetProperty("conflictingCases").GetInt32());
        Assert.Equal(1, await AuditCountAsync("PersonalAccountLinked", staff.Id, personal.Id));   // исполнитель: владелец личного аккаунта
    }

    [Fact]
    public async Task A_link_request_works_once()
    {
        var staff = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (personal, personalToken) = await StaffActor.PersonalAsync(_factory);
        var token = await RequestedAsync(staff, personal);
        await SendAsync(personalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(token)}/confirm");

        var again = await SendAsync(personalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(token)}/confirm");
        var preview = await SendAsync(personalToken, HttpMethod.Get, $"/staff-link-requests/{Uri.EscapeDataString(token)}");

        Assert.Equal("link_request_already_confirmed", await CodeAsync(again));
        Assert.Equal("link_request_already_confirmed", await CodeAsync(preview));
    }

    [Fact]
    public async Task Only_the_addressee_can_see_or_confirm_a_request_and_only_as_a_regular_signed_in_account()
    {
        var staff = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (personal, _) = await StaffActor.PersonalAsync(_factory);
        var (_, strangerToken) = await StaffActor.PersonalAsync(_factory);
        var token = await RequestedAsync(staff, personal);
        var path = $"/staff-link-requests/{Uri.EscapeDataString(token)}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(strangerToken, HttpMethod.Get, path)).StatusCode);   // чужой запрос выглядит несуществующим
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(strangerToken, HttpMethod.Post, path + "/confirm")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(strangerToken, HttpMethod.Post, path + "/decline")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(staff.Token, HttpMethod.Post, path + "/confirm")).StatusCode);   // служебный подтвердить не может
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(strangerToken, HttpMethod.Get, "/staff-link-requests/unknown-token")).StatusCode);
        Assert.Null((await UserAsync(staff.Id)).LinkedUserId);
    }

    [Fact]
    public async Task Declining_closes_the_request_and_tells_the_staff_member()
    {
        var staff = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (personal, personalToken) = await StaffActor.PersonalAsync(_factory);
        var token = await RequestedAsync(staff, personal);
        var path = $"/staff-link-requests/{Uri.EscapeDataString(token)}";

        var decline = await SendAsync(personalToken, HttpMethod.Post, path + "/decline");
        var confirm = await SendAsync(personalToken, HttpMethod.Post, path + "/confirm");

        Assert.Equal(HttpStatusCode.NoContent, decline.StatusCode);
        Assert.Equal("link_request_declined", await CodeAsync(confirm));
        Assert.Null((await UserAsync(staff.Id)).LinkedUserId);
        Assert.Single(await NotificationsAsync(staff.Token, "PersonalLinkDeclined"));
        Assert.Equal(1, await AuditCountAsync("PersonalLinkDeclined", staff.Id, personal.Id));
        var state = await JsonAsync(await SendAsync(staff.Token, HttpMethod.Get, "/me/staff-link"));
        Assert.Equal(JsonValueKind.Null, state.GetProperty("pending").ValueKind);
    }

    [Fact]
    public async Task A_cancelled_or_replaced_request_stops_working()
    {
        var staff = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (personal, personalToken) = await StaffActor.PersonalAsync(_factory);
        var first = await RequestedAsync(staff, personal);
        var second = await RequestedAsync(staff, personal);   // второй запрос заменяет первый

        var firstTry = await SendAsync(personalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(first)}/confirm");
        await SendAsync(staff.Token, HttpMethod.Post, "/me/staff-link/cancel");
        var secondTry = await SendAsync(personalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(second)}/confirm");

        Assert.NotEqual(first, second);
        Assert.Equal("link_request_cancelled", await CodeAsync(firstTry));
        Assert.Equal("link_request_cancelled", await CodeAsync(secondTry));
        Assert.Null((await UserAsync(staff.Id)).LinkedUserId);
    }

    [Fact]
    public async Task An_expired_request_is_refused()
    {
        var staff = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (personal, personalToken) = await StaffActor.PersonalAsync(_factory);
        var token = await RequestedAsync(staff, personal);
        await _factory.QueryAsync(async db =>
        {
            await db.PersonalLinkRequests.Where(r => r.StaffId == staff.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, Now.AddMinutes(-1)));
            return 0;
        });

        var preview = await SendAsync(personalToken, HttpMethod.Get, $"/staff-link-requests/{Uri.EscapeDataString(token)}");
        var confirm = await SendAsync(personalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(token)}/confirm");

        Assert.Equal("link_request_expired", await CodeAsync(preview));
        Assert.Equal("link_request_expired", await CodeAsync(confirm));
        var state = await JsonAsync(await SendAsync(staff.Token, HttpMethod.Get, "/me/staff-link"));
        Assert.Equal(JsonValueKind.Null, state.GetProperty("pending").ValueKind);
    }

    // ======================= один к одному =======================

    [Fact]
    public async Task A_personal_account_links_to_one_staff_account_and_a_staff_account_to_one_personal()
    {
        var first = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var second = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (personal, personalToken) = await StaffActor.PersonalAsync(_factory);
        var (another, _) = await StaffActor.PersonalAsync(_factory);

        // Оба сотрудника успели отправить запрос на один личный аккаунт, пока тот ещё свободен.
        var firstToken = await RequestedAsync(first, personal);
        var secondToken = await RequestedAsync(second, personal);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(personalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(firstToken)}/confirm")).StatusCode);

        var late = await SendAsync(personalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(secondToken)}/confirm");
        var freshRequest = await RequestAsync(second, personal.Email!);
        var secondPersonal = await RequestAsync(first, another.Email!);

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("personal_account_already_linked", await CodeAsync(late));
        Assert.Equal("personal_account_already_linked", await CodeAsync(freshRequest));
        Assert.Equal("staff_already_linked", await CodeAsync(secondPersonal));
        Assert.Null((await UserAsync(second.Id)).LinkedUserId);
        Assert.Equal(personal.Id, (await UserAsync(first.Id)).LinkedUserId);
    }

    // ======================= конфликт интересов =======================

    private async Task<(Guid ExchangeId, Guid CaseId, MatchPersonRef A)> CaseOfAsync()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        await _factory.QueryAsync(async db =>
        {
            // Участник создан напрямую в базе, без Identity: чтобы его нашли по адресу, как настоящего, проставляем нормализованные поля.
            await db.Users.Where(u => u.Id == x.A.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.NormalizedEmail, u => u.Email!.ToUpper()).SetProperty(u => u.NormalizedUserName, u => u.Email!.ToUpper()));
            return 0;
        });
        var created = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new
        {
            type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (x.ExchangeId, (await JsonAsync(created)).GetProperty("id").GetGuid(), new MatchPersonRef(x.A.Id, x.A.Token));
    }

    private sealed record MatchPersonRef(Guid Id, string Token);


    [Fact]
    public async Task The_staff_member_is_told_how_many_of_their_cases_in_work_now_conflict()
    {
        var moderator = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (_, caseId, a) = await CaseOfAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/take")).StatusCode);   // взято до привязки
        var personal = await UserAsync(a.Id);
        var token = await RequestedAsync(moderator, personal);

        await SendAsync(a.Token, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(token)}/confirm");

        var note = Assert.Single(await NotificationsAsync(moderator.Token, "PersonalLinkConfirmed")).GetProperty("payload");
        Assert.Equal(1, note.GetProperty("conflictingCases").GetInt32());
    }

    // ======================= снятие =======================

    // ======================= снятие привязки: только админ, история навсегда =======================

    private async Task<HttpResponseMessage> AdminUnlinkAsync(StaffActor admin, StaffActor staff, object? confirmation = null) =>
        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff/{staff.Id}/personal-link/unlink", new { confirmation = confirmation ?? admin.Confirm() });

    private async Task<(Guid RequestId, JsonElement Dto)> UnlinkRequestedAsync(StaffActor staff, string reason = "Больше не нужна привязка этого аккаунта.")
    {
        var response = await SendAsync(staff.Token, HttpMethod.Post, "/me/staff-link/unlink-request", new { reason });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await JsonAsync(response);
        return (state.GetProperty("unlinkRequest").GetProperty("id").GetGuid(), state);
    }

    /// <summary>Модератор, привязанный к личному аккаунту A обмена, по которому подано обращение: возвращает всё нужное для проверок конфликта.</summary>
    private async Task<(StaffActor Moderator, Guid CaseId, Guid PersonalId, string PersonalToken, Guid PartnerCaseId)> LinkedModeratorWithCasesAsync()
    {
        var moderator = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == x.A.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.NormalizedEmail, u => u.Email!.ToUpper()).SetProperty(u => u.NormalizedUserName, u => u.Email!.ToUpper()));
            return 0;
        });
        var body = new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости." };
        var byA = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", body);
        var byB = await SendAsync(x.B.Token, HttpMethod.Post, "/cases", body);   // обращение партнёра A: тоже конфликт (бывший партнёр личного аккаунта)
        var personal = await UserAsync(x.A.Id);
        var token = await RequestedAsync(moderator, personal);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(x.A.Token, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(token)}/confirm")).StatusCode);
        return (moderator, (await JsonAsync(byA)).GetProperty("id").GetGuid(), x.A.Id, x.A.Token, (await JsonAsync(byB)).GetProperty("id").GetGuid());
    }

    private async Task AssertConflictAsync(StaffActor moderator, params Guid[] caseIds)
    {
        foreach (var id in caseIds)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{id}")).StatusCode);   // из очереди пропало
            var take = await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{id}/take");
            Assert.Equal(HttpStatusCode.Forbidden, take.StatusCode);
            Assert.Equal("conflict_of_interest", await CodeAsync(take));
        }
    }

    [Fact]
    public async Task After_linking_the_moderator_cannot_take_cases_with_their_personal_account_or_its_partners()
    {
        var moderator = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == x.A.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.NormalizedEmail, u => u.Email!.ToUpper()).SetProperty(u => u.NormalizedUserName, u => u.Email!.ToUpper()));
            return 0;
        });
        var body = new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости." };
        var caseId = (await JsonAsync(await SendAsync(x.A.Token, HttpMethod.Post, "/cases", body))).GetProperty("id").GetGuid();
        var token = await RequestedAsync(moderator, await UserAsync(x.A.Id));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{caseId}")).StatusCode);   // пока привязки нет, конфликта нет

        await SendAsync(x.A.Token, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(token)}/confirm");

        await AssertConflictAsync(moderator, caseId);
    }

    [Fact]
    public async Task The_staff_member_cannot_unlink_alone_the_old_route_is_gone_and_a_request_is_only_a_request()
    {
        var (moderator, caseId, personalId, _, partnerCaseId) = await LinkedModeratorWithCasesAsync();

        var oldRoute = await SendAsync(moderator.Token, HttpMethod.Post, "/me/staff-link/unlink", new { confirmation = moderator.Confirm() });
        var (_, state) = await UnlinkRequestedAsync(moderator);

        Assert.True(oldRoute.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Equal(personalId, state.GetProperty("linked").GetProperty("userId").GetGuid());   // привязка на месте
        Assert.Equal(personalId, (await UserAsync(moderator.Id)).LinkedUserId);
        await AssertConflictAsync(moderator, caseId, partnerCaseId);
    }

    [Fact]
    public async Task The_loophole_is_closed_after_an_admin_unlinks_the_former_personal_account_and_its_partners_still_conflict()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var (moderator, caseId, personalId, personalToken, partnerCaseId) = await LinkedModeratorWithCasesAsync();

        var unlink = await AdminUnlinkAsync(admin, moderator);

        Assert.Equal(HttpStatusCode.OK, unlink.StatusCode);
        Assert.Null((await UserAsync(moderator.Id)).LinkedUserId);
        await AssertConflictAsync(moderator, caseId, partnerCaseId);   // сняли привязку, а обращения бывшего личного аккаунта и его партнёра всё равно недоступны

        // Привязать обратно или другой аккаунт: история остаётся, конфликт тоже.
        var (other, otherToken) = await StaffActor.PersonalAsync(_factory);
        var otherLink = await RequestedAsync(moderator, other);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(otherToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(otherLink)}/confirm")).StatusCode);
        await AssertConflictAsync(moderator, caseId, partnerCaseId);
        var history = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/staff/{moderator.Id}/personal-links"));
        Assert.Equal(2, history.GetArrayLength());
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("personal").GetProperty("userId").GetGuid() == personalId && h.GetProperty("unlinkedAt").ValueKind != JsonValueKind.Null);
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("personal").GetProperty("userId").GetGuid() == other.Id && h.GetProperty("isActive").GetBoolean());
        Assert.NotEqual(Guid.Empty, personalId);
        Assert.NotNull(personalToken);
    }

    [Fact]
    public async Task Linking_the_same_personal_account_back_changes_nothing_for_conflicts()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var (moderator, caseId, personalId, personalToken, _) = await LinkedModeratorWithCasesAsync();
        await AdminUnlinkAsync(admin, moderator);

        var token = await RequestedAsync(moderator, await UserAsync(personalId));
        var confirm = await SendAsync(personalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(token)}/confirm");

        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        await AssertConflictAsync(moderator, caseId);
        var history = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/staff/{moderator.Id}/personal-links"));
        Assert.Equal(2, history.GetArrayLength());   // две записи: прежняя снята, новая действует
    }

    [Fact]
    public async Task A_link_made_before_the_history_existed_still_conflicts_and_unlinking_writes_it_into_the_history()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var moderatorRow = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator, x.A);   // привязка записана напрямую, без истории
        var body = new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости." };
        var caseId = (await JsonAsync(await SendAsync(x.A.Token, HttpMethod.Post, "/cases", body))).GetProperty("id").GetGuid();
        var fake = new StaffActor { User = moderatorRow.User, Token = moderatorRow.Token, Key = string.Empty, Recovery = [] };
        await AssertConflictAsync(fake, caseId);
        Assert.Equal(0, await _factory.QueryAsync(db => db.StaffPersonalLinks.CountAsync(l => l.StaffId == moderatorRow.Id)));

        var unlink = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff/{moderatorRow.Id}/personal-link/unlink", new { confirmation = admin.Confirm() });

        Assert.Equal(HttpStatusCode.OK, unlink.StatusCode);
        var row = await _factory.QueryAsync(db => db.StaffPersonalLinks.AsNoTracking().SingleAsync(l => l.StaffId == moderatorRow.Id));
        Assert.Equal(x.A.Id, row.PersonalUserId);
        Assert.Equal(admin.Id, row.UnlinkedById);
        await AssertConflictAsync(fake, caseId);   // конфликт сохранился
    }

    [Fact]
    public async Task A_request_to_unlink_needs_a_reason_a_link_and_is_one_at_a_time_and_admins_are_told()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var superAdmin = await StaffActor.CreateAsync(_factory, UserRole.SuperAdmin);
        var (moderator, _, _, _, _) = await LinkedModeratorWithCasesAsync();
        var notLinked = await StaffActor.CreateAsync(_factory, UserRole.Moderator);

        var none = await SendAsync(notLinked.Token, HttpMethod.Post, "/me/staff-link/unlink-request", new { reason = "Больше не нужна привязка этого аккаунта." });
        var shortReason = await SendAsync(moderator.Token, HttpMethod.Post, "/me/staff-link/unlink-request", new { reason = "мало" });
        var noReason = await SendAsync(moderator.Token, HttpMethod.Post, "/me/staff-link/unlink-request", new { reason = (string?)null });
        var (requestId, state) = await UnlinkRequestedAsync(moderator);
        var twice = await SendAsync(moderator.Token, HttpMethod.Post, "/me/staff-link/unlink-request", new { reason = "Ещё одна причина для снятия." });

        Assert.Equal("not_linked", await CodeAsync(none));
        Assert.Equal("unlink_reason_too_short", await CodeAsync(shortReason));
        Assert.Equal("unlink_reason_too_short", await CodeAsync(noReason));
        Assert.Equal("unlink_request_already_pending", await CodeAsync(twice));
        Assert.Equal("Больше не нужна привязка этого аккаунта.", state.GetProperty("unlinkRequest").GetProperty("reason").GetString());
        Assert.Contains(await NotificationsAsync(admin.Token, "PersonalUnlinkRequested"), n => n.GetProperty("payload").GetProperty("requestId").GetGuid() == requestId);
        Assert.Contains(await NotificationsAsync(superAdmin.Token, "PersonalUnlinkRequested"), n => n.GetProperty("payload").GetProperty("requestId").GetGuid() == requestId);
        Assert.Equal(1, await AuditCountAsync("PersonalUnlinkRequested", moderator.Id, moderator.Id));
    }

    [Fact]
    public async Task An_admin_approves_with_the_confirmation_and_the_link_is_removed_audited_and_the_staff_member_is_told()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var (moderator, _, personalId, _, _) = await LinkedModeratorWithCasesAsync();
        var (requestId, _) = await UnlinkRequestedAsync(moderator);

        var noConfirmation = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/personal-unlink-requests/{requestId}/approve", new { });
        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/personal-unlink-requests/{requestId}/approve", new { confirmation = admin.Confirm() });
        var again = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/personal-unlink-requests/{requestId}/approve", new { confirmation = admin.Confirm() });

        Assert.Equal("step_up_required", await CodeAsync(noConfirmation));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("Approved", dto.GetProperty("status").GetString());
        Assert.Equal(admin.Id, dto.GetProperty("decidedById").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(again));
        Assert.Null((await UserAsync(moderator.Id)).LinkedUserId);
        var audit = await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "PersonalAccountUnlinked" && a.TargetId == moderator.Id));
        Assert.Equal(admin.Id, audit.ActorId);   // исполнитель — админ, а не сам сотрудник
        Assert.Contains(personalId.ToString(), audit.DetailsJson);
        var note = Assert.Single(await NotificationsAsync(moderator.Token, "PersonalUnlinkDecided")).GetProperty("payload");
        Assert.True(note.GetProperty("approved").GetBoolean());
        var state = await JsonAsync(await SendAsync(moderator.Token, HttpMethod.Get, "/me/staff-link"));
        Assert.Equal(JsonValueKind.Null, state.GetProperty("linked").ValueKind);
        Assert.Equal(JsonValueKind.Null, state.GetProperty("unlinkRequest").ValueKind);
    }

    [Fact]
    public async Task A_rejected_request_keeps_the_link_and_the_staff_member_may_ask_again()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var (moderator, _, personalId, _, _) = await LinkedModeratorWithCasesAsync();
        var (requestId, _) = await UnlinkRequestedAsync(moderator);

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/personal-unlink-requests/{requestId}/reject", new { comment = "Причина не убедительна" });
        var again = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/personal-unlink-requests/{requestId}/reject", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Rejected", (await JsonAsync(response)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(personalId, (await UserAsync(moderator.Id)).LinkedUserId);
        var note = Assert.Single(await NotificationsAsync(moderator.Token, "PersonalUnlinkDecided")).GetProperty("payload");
        Assert.False(note.GetProperty("approved").GetBoolean());
        await UnlinkRequestedAsync(moderator, "Новая причина: личный аккаунт закрыт.");   // после отказа можно просить снова
    }

    [Fact]
    public async Task Admins_decide_for_moderators_only_and_superadmins_for_the_rest_and_nobody_for_themselves()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var superAdmin = await StaffActor.CreateAsync(_factory, UserRole.SuperAdmin);
        var otherSuper = await StaffActor.CreateAsync(_factory, UserRole.SuperAdmin);
        var moderator = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var userToken = (await StaffActor.PersonalAsync(_factory)).Token;
        var (adminPersonal, adminPersonalToken) = await StaffActor.PersonalAsync(_factory);
        var (superPersonal, superPersonalToken) = await StaffActor.PersonalAsync(_factory);
        var adminLink = await RequestedAsync(admin, adminPersonal);
        await SendAsync(adminPersonalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(adminLink)}/confirm");
        var superLink = await RequestedAsync(otherSuper, superPersonal);
        await SendAsync(superPersonalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(superLink)}/confirm");
        var (adminRequest, _) = await UnlinkRequestedAsync(admin);

        var byAdminForAdmin = await AdminUnlinkAsync(admin, admin);          // себе никто
        var byAdminForSuper = await AdminUnlinkAsync(admin, otherSuper);     // админ суперадмину не может
        var approveByAdmin = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/personal-unlink-requests/{adminRequest}/approve", new { confirmation = admin.Confirm() });
        var bySuperForSelf = await AdminUnlinkAsync(otherSuper, otherSuper);
        var listForAdmin = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/personal-unlink-requests?pageSize=50"));
        var listForSuper = await JsonAsync(await SendAsync(superAdmin.Token, HttpMethod.Get, "/admin/personal-unlink-requests?pageSize=50"));

        Assert.Equal("cannot_change_own_rights", await CodeAsync(byAdminForAdmin));
        Assert.Equal("staff_role_not_allowed", await CodeAsync(byAdminForSuper));
        Assert.Equal("cannot_change_own_rights", await CodeAsync(approveByAdmin));   // свой запрос сам не решить
        Assert.Equal("cannot_change_own_rights", await CodeAsync(bySuperForSelf));
        Assert.DoesNotContain(listForAdmin.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == adminRequest);   // запросы админов админ не видит
        Assert.Contains(listForSuper.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == adminRequest);
        Assert.Equal(HttpStatusCode.OK, (await AdminUnlinkAsync(superAdmin, admin)).StatusCode);   // суперадмин снимает админу
        Assert.Equal(HttpStatusCode.OK, (await AdminUnlinkAsync(superAdmin, otherSuper)).StatusCode);   // и другому суперадмину
        Assert.Null((await UserAsync(admin.Id)).LinkedUserId);
        Assert.Null((await UserAsync(otherSuper.Id)).LinkedUserId);

        // Остальные роли к админским маршрутам не допущены.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Get, "/admin/personal-unlink-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(userToken, HttpMethod.Get, "/admin/personal-unlink-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Post, $"/admin/staff/{admin.Id}/personal-link/unlink", new { confirmation = moderator.Confirm() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/admin/personal-unlink-requests")).StatusCode);
    }

    [Fact]
    public async Task Unlinking_directly_closes_a_pending_request_needs_the_confirmation_and_a_link()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var (moderator, _, _, _, _) = await LinkedModeratorWithCasesAsync();
        var notLinked = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (requestId, _) = await UnlinkRequestedAsync(moderator);

        var none = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff/{moderator.Id}/personal-link/unlink", new { });
        var wrong = await AdminUnlinkAsync(admin, moderator, new { password = "wrong-password-1", code = admin.Recovery[9], isRecoveryCode = true });
        Assert.Equal("step_up_required", await CodeAsync(none));
        Assert.Equal("invalid_password", await CodeAsync(wrong));
        Assert.NotNull((await UserAsync(moderator.Id)).LinkedUserId);   // отказы привязку не сняли

        Assert.Equal(HttpStatusCode.OK, (await AdminUnlinkAsync(admin, moderator)).StatusCode);
        var request = await _factory.QueryAsync(db => db.PersonalUnlinkRequests.AsNoTracking().SingleAsync(r => r.Id == requestId));
        var again = await AdminUnlinkAsync(admin, moderator);
        var neverLinked = await AdminUnlinkAsync(admin, notLinked);

        Assert.Equal(UnlinkRequestStatus.Approved, request.Status);
        Assert.Equal("not_linked", await CodeAsync(again));
        Assert.Equal("not_linked", await CodeAsync(neverLinked));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/staff/{Guid.NewGuid()}/personal-link/unlink", new { confirmation = admin.Confirm() })).StatusCode);
    }

    [Fact]
    public async Task After_an_admin_unlinks_the_personal_account_can_be_linked_to_another_staff_member_who_has_no_conflict_with_it_yet()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var first = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var second = await StaffActor.CreateAsync(_factory, UserRole.Moderator);
        var (personal, personalToken) = await StaffActor.PersonalAsync(_factory);
        var token = await RequestedAsync(first, personal);
        await SendAsync(personalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(token)}/confirm");
        await AdminUnlinkAsync(admin, first);

        var secondToken = await RequestedAsync(second, personal);
        var confirm = await SendAsync(personalToken, HttpMethod.Post, $"/staff-link-requests/{Uri.EscapeDataString(secondToken)}/confirm");

        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        Assert.Equal(personal.Id, (await UserAsync(second.Id)).LinkedUserId);
        Assert.Null((await UserAsync(first.Id)).LinkedUserId);
    }

    [Fact]
    public async Task The_history_is_visible_to_admins_with_rights_and_a_missing_staff_member_is_not_found()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var other = await StaffActor.CreateAsync(_factory, UserRole.Admin);

        var forbidden = await SendAsync(admin.Token, HttpMethod.Get, $"/admin/staff/{other.Id}/personal-links");
        var missing = await SendAsync(admin.Token, HttpMethod.Get, $"/admin/staff/{Guid.NewGuid()}/personal-links");

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
