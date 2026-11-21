using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Staff;

/// <summary>Журнал аудита для панелей: суперадмин видит всё, админ только действия модераторов и свои, адреса видит только суперадмин; фильтры, страницы, права.</summary>
[Collection(ApiCollection.Name)]
public class AuditJournalApiTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public AuditJournalApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private async Task<HttpResponseMessage> SendAsync(string? token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1" + url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    private static string Marker() => "T" + Guid.NewGuid().ToString("N")[..12];

    private async Task WriteAsync(Guid? actor, string action, DateTime at, Guid? target = null, string targetType = "Test", string details = "{}", string? ip = null)
    {
        await _factory.QueryAsync(async db =>
        {
            db.AuditLogs.Add(AuditLog.Create(actor, action, targetType, target, details, ip, at));
            await db.SaveChangesAsync();
            return 0;
        });
    }

    private sealed record World(StaffPerson Moderator, StaffPerson Admin, StaffPerson OtherAdmin, StaffPerson SuperAdmin, string Action);

    /// <summary>Записи с одним уникальным названием действия от всех видов исполнителей, включая систему.</summary>
    private async Task<World> WorldAsync()
    {
        var w = new World(
            await ModerationTestData.StaffAsync(_factory, UserRole.Moderator), await ModerationTestData.StaffAsync(_factory, UserRole.Admin),
            await ModerationTestData.StaffAsync(_factory, UserRole.Admin), await ModerationTestData.StaffAsync(_factory, UserRole.SuperAdmin), Marker());
        await WriteAsync(w.Moderator.Id, w.Action, Now.AddMinutes(-5), details: "{\"by\":\"moderator\"}", ip: "10.0.0.1");
        await WriteAsync(w.Admin.Id, w.Action, Now.AddMinutes(-4), details: "{\"by\":\"admin\"}", ip: "10.0.0.2");
        await WriteAsync(w.OtherAdmin.Id, w.Action, Now.AddMinutes(-3), details: "{\"by\":\"other-admin\"}", ip: "10.0.0.3");
        await WriteAsync(w.SuperAdmin.Id, w.Action, Now.AddMinutes(-2), details: "{\"by\":\"superadmin\"}", ip: "10.0.0.4");
        await WriteAsync(null, w.Action, Now.AddMinutes(-1), details: "{\"by\":\"system\"}");
        return w;
    }

    private static List<string> Who(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("details").GetProperty("by").GetString()!).ToList();

    // ======================= кто что видит =======================

    [Fact]
    public async Task The_superadmin_sees_everything_including_the_system_and_the_addresses()
    {
        var w = await WorldAsync();

        var page = await JsonAsync(await SendAsync(w.SuperAdmin.Token, $"/superadmin/audit?action={w.Action}"));

        Assert.Equal(["system", "superadmin", "other-admin", "admin", "moderator"], Who(page));   // новые сверху
        Assert.Equal(5, page.GetProperty("totalCount").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("actorId").ValueKind);       // действие системы
        Assert.Equal("10.0.0.4", items[1].GetProperty("ipAddress").GetString());
        Assert.Equal(w.Moderator.Id, items[4].GetProperty("actorId").GetGuid());
        Assert.Equal("Moderator", items[4].GetProperty("actorRole").GetString());
        Assert.Equal(w.Moderator.User.Name, items[4].GetProperty("actorName").GetString());
        Assert.Equal("Test", items[4].GetProperty("targetType").GetString());
    }

    [Fact]
    public async Task An_admin_sees_the_moderators_and_their_own_actions_only_and_never_the_addresses()
    {
        var w = await WorldAsync();

        var page = await JsonAsync(await SendAsync(w.Admin.Token, $"/admin/audit?action={w.Action}"));

        Assert.Equal(["admin", "moderator"], Who(page));   // чужого админа, суперадмина и системы нет
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.All(page.GetProperty("items").EnumerateArray(), i => Assert.Equal(JsonValueKind.Null, i.GetProperty("ipAddress").ValueKind));
        var other = await JsonAsync(await SendAsync(w.OtherAdmin.Token, $"/admin/audit?action={w.Action}"));
        Assert.Equal(["other-admin", "moderator"], Who(other));
        var foreign = await JsonAsync(await SendAsync(w.Admin.Token, $"/admin/audit?action={w.Action}&actorId={w.OtherAdmin.Id}"));   // фильтр не расширяет видимость
        Assert.Equal(0, foreign.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task The_superadmin_on_the_admin_route_still_sees_everything()
    {
        var w = await WorldAsync();

        var page = await JsonAsync(await SendAsync(w.SuperAdmin.Token, $"/admin/audit?action={w.Action}"));

        Assert.Equal(5, page.GetProperty("totalCount").GetInt32());
        Assert.Equal("10.0.0.1", page.GetProperty("items").EnumerateArray().Last().GetProperty("ipAddress").GetString());
    }

    [Fact]
    public async Task A_real_admin_action_on_a_moderator_is_visible_to_the_admin_who_did_it_and_to_the_superadmin_only()
    {
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var otherAdmin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var superAdmin = await ModerationTestData.StaffAsync(_factory, UserRole.SuperAdmin);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);

        var removed = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/staff/moderators/{moderator.Id}/remove")
        {
            Content = JsonContent.Create(new { reason = "Модератор больше не работает в команде.", confirmation = admin.Confirm() }),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", admin.Token) },
        });
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var mine = await JsonAsync(await SendAsync(admin.Token, $"/admin/audit?action=StaffRemoved&targetId={moderator.Id}"));
        var theirs = await JsonAsync(await SendAsync(otherAdmin.Token, $"/admin/audit?action=StaffRemoved&targetId={moderator.Id}"));
        var full = await JsonAsync(await SendAsync(superAdmin.Token, $"/superadmin/audit?action=StaffRemoved&targetId={moderator.Id}"));

        Assert.Equal(1, mine.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, theirs.GetProperty("totalCount").GetInt32());
        var entry = Assert.Single(full.GetProperty("items").EnumerateArray());
        Assert.Equal(admin.Id, entry.GetProperty("actorId").GetGuid());
        Assert.Equal("Модератор больше не работает в команде.", entry.GetProperty("details").GetProperty("reason").GetString());
    }

    // ======================= доступ =======================

    [Fact]
    public async Task Only_admins_see_the_admin_journal_and_only_superadmins_the_full_one()
    {
        var w = await WorldAsync();
        var (_, personal) = await StaffActor.PersonalAsync(_factory);
        var removed = await ModerationTestData.StaffAsync(_factory, UserRole.SuperAdmin);
        await _factory.QueryAsync(async db =>
        {
            await Task.CompletedTask;
            db.Users.Where(u => u.Id == removed.Id).ToList().ForEach(u => u.RemoveFromStaff());
            await db.SaveChangesAsync();
            return 0;
        });

        foreach (var url in new[] { "/admin/audit", "/admin/audit/actions", "/superadmin/audit", "/superadmin/audit/actions" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(personal, url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(w.Moderator.Token, url)).StatusCode);
            Assert.Equal("staff_not_active", await CodeAsync(await SendAsync(removed.Token, url)));
        }

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(w.Admin.Token, "/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(w.Admin.Token, "/superadmin/audit")).StatusCode);          // админ полный журнал не видит
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(w.Admin.Token, "/superadmin/audit/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(w.SuperAdmin.Token, "/superadmin/audit")).StatusCode);
    }

    // ======================= фильтры, страницы, список действий =======================

    [Fact]
    public async Task Filters_by_actor_target_period_and_pages_work_together()
    {
        var w = await WorldAsync();
        var target = Guid.NewGuid();
        var action = Marker();
        for (var i = 0; i < 5; i++)
        {
            await WriteAsync(w.Moderator.Id, action, Now.AddHours(-10).AddMinutes(i), target, "Case", "{\"by\":\"m" + i + "\"}");
        }

        await WriteAsync(w.Moderator.Id, action, Now.AddHours(-10).AddMinutes(9), Guid.NewGuid(), "Exchange", "{\"by\":\"other\"}");

        var byTarget = await JsonAsync(await SendAsync(w.SuperAdmin.Token, $"/superadmin/audit?action={action}&targetType=Case&targetId={target}&pageSize=2"));
        var second = await JsonAsync(await SendAsync(w.SuperAdmin.Token, $"/superadmin/audit?action={action}&targetId={target}&pageSize=2&page=2"));
        var period = await JsonAsync(await SendAsync(w.SuperAdmin.Token,
            $"/superadmin/audit?action={action}&targetId={target}&from={Uri.EscapeDataString(Now.AddHours(-10).AddMinutes(1).ToString("o"))}&to={Uri.EscapeDataString(Now.AddHours(-10).AddMinutes(4).ToString("o"))}"));
        var byActor = await JsonAsync(await SendAsync(w.SuperAdmin.Token, $"/superadmin/audit?action={action}&actorId={w.Moderator.Id}"));
        var byType = await JsonAsync(await SendAsync(w.SuperAdmin.Token, $"/superadmin/audit?action={action}&targetType=Exchange"));

        Assert.Equal(5, byTarget.GetProperty("totalCount").GetInt32());
        Assert.Equal(["m4", "m3"], Who(byTarget));
        Assert.Equal(["m2", "m1"], Who(second));
        Assert.Equal(["m3", "m2", "m1"], Who(period));   // начало включительно, конец не включая
        Assert.Equal(6, byActor.GetProperty("totalCount").GetInt32());
        Assert.Equal(["other"], Who(byType));
    }

    [Fact]
    public async Task Bad_filters_are_refused()
    {
        var w = await WorldAsync();
        var from = Uri.EscapeDataString(Now.ToString("o"));
        var to = Uri.EscapeDataString(Now.AddDays(-1).ToString("o"));

        Assert.Equal("invalid_paging", await CodeAsync(await SendAsync(w.SuperAdmin.Token, "/superadmin/audit?pageSize=500")));
        Assert.Equal("invalid_paging", await CodeAsync(await SendAsync(w.SuperAdmin.Token, "/superadmin/audit?page=0")));
        Assert.Equal("invalid_period", await CodeAsync(await SendAsync(w.SuperAdmin.Token, $"/superadmin/audit?from={from}&to={to}")));
        Assert.Equal("invalid_filter", await CodeAsync(await SendAsync(w.SuperAdmin.Token, $"/superadmin/audit?action={new string('a', 65)}")));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(w.SuperAdmin.Token, "/superadmin/audit?actorId=not-a-guid")).StatusCode);
    }

    [Fact]
    public async Task The_action_list_is_scoped_like_the_journal()
    {
        var w = await WorldAsync();
        var onlyOther = Marker();
        await WriteAsync(w.OtherAdmin.Id, onlyOther, Now);
        var onlyModerator = Marker();
        await WriteAsync(w.Moderator.Id, onlyModerator, Now);

        var forAdmin = (await JsonAsync(await SendAsync(w.Admin.Token, "/admin/audit/actions"))).EnumerateArray().Select(x => x.GetString()).ToList();
        var forSuper = (await JsonAsync(await SendAsync(w.SuperAdmin.Token, "/superadmin/audit/actions"))).EnumerateArray().Select(x => x.GetString()).ToList();

        Assert.Contains(onlyModerator, forAdmin);
        Assert.DoesNotContain(onlyOther, forAdmin);      // действие чужого админа админу не видно
        Assert.Contains(onlyOther, forSuper);
        Assert.Equal(forSuper.Order().ToList(), forSuper);
    }

    [Fact]
    public async Task Details_that_are_not_an_object_are_returned_as_they_are()
    {
        var w = await WorldAsync();
        var action = Marker();
        await _factory.QueryAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"AuditLogs\" (\"Id\", \"ActorId\", \"Action\", \"TargetType\", \"TargetId\", \"DetailsJson\", \"IpAddress\", \"CreatedAt\") VALUES ({Guid.NewGuid()}, {w.Moderator.Id}, {action}, 'Test', NULL, '\"just a string\"'::jsonb, NULL, {Now})");
            return 0;
        });

        var page = await JsonAsync(await SendAsync(w.SuperAdmin.Token, $"/superadmin/audit?action={action}"));

        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("just a string", item.GetProperty("details").GetString());   // не объект, но корректный JSON: отдаётся как есть
    }
}
