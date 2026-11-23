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
        var client = app.CreateClient(NoRedirect);

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
        var client = app.CreateClient(NoRedirect);

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

    [Fact]
    public async Task A_repeated_admin_seed_adds_new_data_and_replaces_the_pending_link_request_of_the_same_moderator()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateClient(NoRedirect);

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
        var client = production.CreateClient(NoRedirect);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/dev/seed-admin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/dev/seed-staff", null)).StatusCode);
    }
}
