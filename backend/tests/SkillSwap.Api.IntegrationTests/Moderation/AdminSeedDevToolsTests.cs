using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>
/// Сидеры для панелей админа и суперадмина (этап 7, шаг 10): суперадмин в <c>/dev/seed-staff</c> и <c>POST /dev/seed-admin</c> с данными для экранов
/// (предложения навыков, запрос на бан, приглашение, запрос на привязку). Работают только в Development.
/// </summary>
[Collection(ApiCollection.Name)]
public class AdminSeedDevToolsTests
{
    private static readonly WebApplicationFactoryClientOptions NoRedirect = new() { AllowAutoRedirect = false };

    private readonly ApiFactory _factory;

    public AdminSeedDevToolsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string? token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client.SendAsync(request);
    }

    [Fact]
    public async Task The_staff_seed_also_creates_a_superadmin_whose_token_opens_the_superadmin_routes_only()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateLocalClient(NoRedirect);

        var first = await JsonAsync(await client.PostAsync("/dev/seed-staff", null));
        var second = await JsonAsync(await client.PostAsync("/dev/seed-staff", null));

        var superAdmin = second.GetProperty("superAdmin");
        Assert.Equal("SuperAdmin", superAdmin.GetProperty("role").GetString());
        Assert.Equal("dev-superadmin@example.com", superAdmin.GetProperty("email").GetString());
        Assert.Equal(first.GetProperty("superAdmin").GetProperty("userId").GetGuid(), superAdmin.GetProperty("userId").GetGuid());   // создаётся один раз
        Assert.Equal("Dev-staff-pass-1", superAdmin.GetProperty("password").GetString());
        Assert.Equal(2, superAdmin.GetProperty("twoFactor").GetProperty("codes").GetArrayLength());
        var superToken = superAdmin.GetProperty("accessToken").GetString()!;
        var adminToken = second.GetProperty("admin").GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, superToken, "/api/v1/superadmin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, superToken, "/api/v1/admin/users")).StatusCode);   // суперадмин видит и админские разделы
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, adminToken, "/api/v1/superadmin/settings")).StatusCode);
    }

    [Fact]
    public async Task The_admin_seed_creates_data_for_every_admin_screen_with_working_links()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateLocalClient(NoRedirect);

        var response = await client.PostAsync("/dev/seed-admin", null);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var seed = await JsonAsync(response);
        var adminToken = seed.GetProperty("admin").GetProperty("accessToken").GetString()!;
        Assert.Equal("SuperAdmin", seed.GetProperty("superAdmin").GetProperty("role").GetString());

        // Предложения навыков: два одинаковых и одно другое.
        var duplicate = seed.GetProperty("skillSuggestions").GetProperty("duplicateName").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, adminToken, "/api/v1/admin/skill-suggestions?pageSize=50")).StatusCode);
        var pending = await _factory.QueryAsync(db => db.SkillSuggestions.AsNoTracking().Where(s => s.SuggestedName == duplicate && s.Status == SuggestionStatus.Pending).CountAsync());
        Assert.Equal(2, pending);

        // Запрос на бан ждёт решения админа.
        var banId = seed.GetProperty("banRequestId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, adminToken, "/api/v1/admin/ban-requests?pageSize=50")).StatusCode);
        Assert.Equal(BanRequestStatus.Pending, (await _factory.QueryAsync(db => db.BanRequests.AsNoTracking().SingleAsync(b => b.Id == banId))).Status);

        // Приглашение: ссылка ведёт к действующему приглашению, токен берётся из самой ссылки.
        var invitationUrl = seed.GetProperty("invitation").GetProperty("url").GetString()!;
        var invitationToken = Uri.UnescapeDataString(invitationUrl[(invitationUrl.LastIndexOf('/') + 1)..]);
        var preview = await GetAsync(client, null, $"/api/v1/staff-invitations/{invitationToken}");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(seed.GetProperty("invitation").GetProperty("email").GetString(), (await JsonAsync(preview)).GetProperty("email").GetString());

        // Запрос на привязку: его видит владелец личного аккаунта (userB).
        var linkUrl = seed.GetProperty("linkRequest").GetProperty("url").GetString()!;
        var linkToken = Uri.UnescapeDataString(linkUrl[(linkUrl.LastIndexOf('/') + 1)..]);
        var personalId = seed.GetProperty("linkRequest").GetProperty("personalUserId").GetGuid();
        var personal = seed.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("userId").GetGuid() == personalId);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, personal.GetProperty("accessToken").GetString(), $"/api/v1/staff-link-requests/{linkToken}")).StatusCode);

        // Личные пользователи созданы с паролем.
        Assert.Equal(3, seed.GetProperty("users").GetArrayLength());
        Assert.All(seed.GetProperty("users").EnumerateArray(), u => Assert.False(string.IsNullOrEmpty(u.GetProperty("password").GetString())));
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task The_admin_seed_gives_a_real_active_warning_that_the_admin_can_revoke()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateLocalClient(NoRedirect);
        var seed = await JsonAsync(await client.PostAsync("/dev/seed-admin", null));
        var adminToken = seed.GetProperty("admin").GetProperty("accessToken").GetString()!;
        var warning = seed.GetProperty("warning");
        var userId = warning.GetProperty("userId").GetGuid();
        var warningId = warning.GetProperty("id").GetGuid();

        var card = await JsonAsync(await GetAsync(client, adminToken, $"/api/v1/admin/users/{userId}"));

        Assert.Equal(1, card.GetProperty("user").GetProperty("activeWarnings").GetInt32());
        var issued = Assert.Single(card.GetProperty("warnings").EnumerateArray());
        Assert.Equal(warningId, issued.GetProperty("id").GetGuid());
        Assert.True(issued.GetProperty("isActive").GetBoolean());
        Assert.Equal(warning.GetProperty("caseId").GetGuid(), issued.GetProperty("caseId").GetGuid());   // настоящее обращение, не запись «из воздуха»

        var revoke = await PostAsync(client, adminToken, $"/api/v1/admin/warnings/{warningId}/revoke", new { reason = "Проверка снятия предупреждения из сидера." });

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var after = await JsonAsync(revoke);
        Assert.Equal(0, after.GetProperty("user").GetProperty("activeWarnings").GetInt32());
        Assert.False(after.GetProperty("warnings")[0].GetProperty("isActive").GetBoolean());
    }

    /// <summary>Есть ли в списке админов запись по условию: в общей базе админов много (их создают и другие тесты), поэтому листаем все страницы, а не смотрим первую.</summary>
    private async Task<bool> ListedAsync(HttpClient client, string token, bool removed, Func<JsonElement, bool> match)
    {
        for (var page = 1; ; page++)
        {
            var body = await JsonAsync(await GetAsync(client, token, $"/api/v1/admin/staff/admins?pageSize=50&page={page}&removed={removed.ToString().ToLowerInvariant()}"));
            var items = body.GetProperty("items").EnumerateArray().ToList();
            if (items.Any(match))
            {
                return true;
            }

            if (items.Count == 0 || page * 50 >= body.GetProperty("totalCount").GetInt32())
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task The_admin_seed_gives_a_new_removable_admin_each_time_and_the_superadmin_can_remove_it()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateLocalClient(NoRedirect);
        var first = await JsonAsync(await client.PostAsync("/dev/seed-admin", null));
        var second = await JsonAsync(await client.PostAsync("/dev/seed-admin", null));
        var removable = second.GetProperty("removableAdmin");
        var removableId = removable.GetProperty("userId").GetGuid();
        Assert.NotEqual(first.GetProperty("removableAdmin").GetProperty("userId").GetGuid(), removableId);   // каждый раз новый: снятого повторно не снять
        Assert.Equal("Admin", removable.GetProperty("role").GetString());
        Assert.NotEqual(second.GetProperty("admin").GetProperty("userId").GetGuid(), removableId);            // постоянный dev-admin остаётся для входа

        var superAdmin = second.GetProperty("superAdmin");
        var token = superAdmin.GetProperty("accessToken").GetString()!;
        Assert.True(await ListedAsync(client, token, removed: false, a => a.GetProperty("id").GetGuid() == removableId));

        // Подтверждение действия: пароль и текущий код 2FA суперадмина (сидер отдаёт текущий и следующий)
        HttpResponseMessage? removed = null;
        foreach (var code in superAdmin.GetProperty("twoFactor").GetProperty("codes").EnumerateArray().Select(c => c.GetString()!))
        {
            removed = await PostAsync(client, token, $"/api/v1/admin/staff/admins/{removableId}/remove", new
            {
                reason = "Проверка снятия админа из сидера.",
                confirmation = new { password = superAdmin.GetProperty("password").GetString(), code, isRecoveryCode = false },
            });
            if (removed.StatusCode == HttpStatusCode.OK)
            {
                break;
            }
        }

        Assert.True(removed!.StatusCode == HttpStatusCode.OK, await removed.Content.ReadAsStringAsync());
        Assert.True(await ListedAsync(client, token, removed: true, a => a.GetProperty("id").GetGuid() == removableId && a.GetProperty("removed").GetBoolean()));
    }

    [Fact]
    public async Task A_repeated_admin_seed_adds_new_data_and_replaces_the_pending_link_request_of_the_same_moderator()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateLocalClient(NoRedirect);

        var first = await JsonAsync(await client.PostAsync("/dev/seed-admin", null));
        var second = await JsonAsync(await client.PostAsync("/dev/seed-admin", null));

        Assert.Equal(first.GetProperty("admin").GetProperty("userId").GetGuid(), second.GetProperty("admin").GetProperty("userId").GetGuid());   // служебные аккаунты те же
        Assert.NotEqual(first.GetProperty("banRequestId").GetGuid(), second.GetProperty("banRequestId").GetGuid());                          // данные новые
        var oldLink = first.GetProperty("linkRequest").GetProperty("id").GetGuid();
        var newLink = second.GetProperty("linkRequest").GetProperty("id").GetGuid();
        var rows = await _factory.QueryAsync(db => db.PersonalLinkRequests.AsNoTracking().Where(r => r.Id == oldLink || r.Id == newLink).ToListAsync());
        Assert.NotNull(rows.Single(r => r.Id == oldLink).CancelledAt);   // у одного сотрудника ожидающий запрос один
        Assert.Null(rows.Single(r => r.Id == newLink).CancelledAt);
    }

    [Fact]
    public async Task The_admin_seed_does_not_exist_outside_development()
    {
        using var production = _factory.WithSettings("Production");
        var client = production.CreateLocalClient(NoRedirect);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/dev/seed-admin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/dev/seed-staff", null)).StatusCode);
    }
}
