using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.Auth;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Staff;

/// <summary>
/// Общая политика авторизации служебных маршрутов (раздел 3 ТЗ): роль в токене и действующий аккаунт по базе на каждый запрос. Снятый, заблокированный, удалённый или разжалованный
/// сотрудник со ещё живым токеном получает 403 на всех маршрутах /admin, /superadmin и /moderation, в том числе на чтении чата и материалов обращения.
/// </summary>
[Collection(ApiCollection.Name)]
public class ActiveStaffPolicyTests
{
    private const string Complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ActiveStaffPolicyTests(ApiFactory factory)
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

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    private Task SetStatusAsync(Guid id, UserStatus status) => _factory.QueryAsync(async db =>
    {
        await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Status, status));
        return 0;
    });

    private async Task<(Guid CaseId, Guid ExchangeId)> OpenCaseAsync()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var created = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = Complaint });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return ((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(), x.ExchangeId);
    }

    [Fact]
    public async Task A_removed_moderator_with_a_live_token_cannot_take_read_the_chat_or_open_the_queue()
    {
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var (held, _) = await OpenCaseAsync();
        var (waiting, _) = await OpenCaseAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{held}/take")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{held}/chat")).StatusCode);   // пока работает

        // Снимаем «грязно»: статус сменился, а доступ к чату по обращению остался действующим. Решает политика, а не отзыв доступа.
        await SetStatusAsync(moderator.Id, UserStatus.Blocked);

        var take = await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{waiting}/take");
        var chat = await SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{held}/chat");
        var queue = await SendAsync(moderator.Token, HttpMethod.Get, "/moderation/cases");
        var card = await SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{held}");
        var explanations = await SendAsync(moderator.Token, HttpMethod.Get, $"/moderation/cases/{held}/explanations");
        var resolve = await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{held}/resolve", new { decision = "NoViolation", alsoWarned = false, reason = "Переписку изучил: нарушения нет, жалоба необоснованна." });

        foreach (var response in new[] { take, chat, queue, card, explanations, resolve })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("staff_not_active", await CodeAsync(response));
        }

        Assert.Equal(CaseStatus.Open, (await _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(c => c.Id == waiting))).Status);   // не взято
    }

    [Theory]
    [InlineData(UserStatus.Blocked)]
    [InlineData(UserStatus.Deleted)]
    public async Task A_removed_admin_with_a_live_token_is_refused_everywhere_including_the_reads(UserStatus status)
    {
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var (caseId, _) = await OpenCaseAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Get, $"/admin/cases/{caseId}")).StatusCode);   // пока работает
        await SetStatusAsync(admin.Id, status);

        var requests = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Get, "/admin/users"),
            (HttpMethod.Get, "/admin/cases"),
            (HttpMethod.Get, $"/admin/cases/{caseId}"),                       // материалы обращения
            (HttpMethod.Get, "/admin/ban-requests"),
            (HttpMethod.Get, "/admin/moderators"),
            (HttpMethod.Get, "/admin/staff/moderators"),
            (HttpMethod.Get, "/admin/staff-invitations"),
            (HttpMethod.Get, "/admin/skills"),                                // чтение каталога
            (HttpMethod.Get, "/admin/skill-categories"),
            (HttpMethod.Get, "/admin/skill-suggestions"),
            (HttpMethod.Get, "/admin/personal-unlink-requests"),
        };
        foreach (var (method, url) in requests)
        {
            var response = await SendAsync(admin.Token, method, url);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url}: {(int)response.StatusCode}");
            Assert.Equal("staff_not_active", await CodeAsync(response));
        }

        Assert.Equal("staff_not_active", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Post, "/admin/skill-categories", new { nameTg = "x", nameRu = "x", nameEn = "x" + Guid.NewGuid().ToString("N")[..8], sortOrder = 1 })));
        Assert.Equal("staff_not_active", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Post, $"/admin/users/{Guid.NewGuid()}/block", new { reason = "Причина блокировки пользователя." })));
    }

    [Fact]
    public async Task A_staff_member_whose_role_changed_in_the_database_is_refused_with_the_old_token()
    {
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);   // токен выпущен для роли Admin
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == admin.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Role, UserRole.Moderator));
            return 0;
        });

        var response = await SendAsync(admin.Token, HttpMethod.Get, "/admin/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("staff_not_active", await CodeAsync(response));
    }

    [Fact]
    public async Task Staff_policies_do_not_leak_to_other_roles_and_active_staff_work_as_before()
    {
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var superAdmin = await ModerationTestData.StaffAsync(_factory, UserRole.SuperAdmin);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var (_, personal) = await StaffActor.PersonalAsync(_factory);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Get, "/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(superAdmin.Token, HttpMethod.Get, "/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Get, "/moderation/cases")).StatusCode);
        var wrongRole = await SendAsync(moderator.Token, HttpMethod.Get, "/admin/users");
        var regular = await SendAsync(personal, HttpMethod.Get, "/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, regular.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(admin.Token, HttpMethod.Get, "/moderation/cases")).StatusCode);   // админ не модератор
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/admin/users")).StatusCode);
    }

    [Fact]
    public void Every_admin_superadmin_and_moderation_route_uses_a_staff_policy_and_never_a_bare_role()
    {
        var allowed = new[] { Policies.Admin, Policies.SuperAdmin, Policies.Moderator, Policies.Staff };
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var guarded = endpoints.Where(e => e.RoutePattern.RawText is { } t
            && (t.StartsWith("api/v1/admin", StringComparison.Ordinal) || t.StartsWith("api/v1/superadmin", StringComparison.Ordinal) || t.StartsWith("api/v1/moderation", StringComparison.Ordinal))).ToList();

        Assert.True(guarded.Count > 40);   // маршруты действительно нашлись
        foreach (var endpoint in guarded)
        {
            var route = endpoint.RoutePattern.RawText;
            var data = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Assert.True(data.Count > 0, $"{route}: нет авторизации");
            Assert.DoesNotContain(endpoint.Metadata, m => m is IAllowAnonymous);
            Assert.All(data, d =>
            {
                Assert.True(string.IsNullOrEmpty(d.Roles), $"{route}: голая роль {d.Roles}");
                Assert.True(d.Policy is null || allowed.Contains(d.Policy), $"{route}: политика {d.Policy}");
            });
            Assert.Contains(data, d => d.Policy is not null);   // хотя бы одна служебная политика
        }

        // И нигде в приложении нет авторизации голой ролью: любая роль проходит только через политику с проверкой базы.
        foreach (var endpoint in endpoints)
        {
            Assert.All(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), d => Assert.True(string.IsNullOrEmpty(d.Roles), $"{endpoint.RoutePattern.RawText}: голая роль {d.Roles}"));
        }
    }
}
