using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Skills;

/// <summary>Рассмотрение предложений навыков админом: очередь, одобрение, объединение, отказ, уведомления авторам, аудит, права.</summary>
[Collection(ApiCollection.Name)]
public class SkillSuggestionsAdminTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public SkillSuggestionsAdminTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static string Marker() => "Z" + Guid.NewGuid().ToString("N")[..10];

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

    private async Task<Guid> CategoryAsync(StaffPerson admin)
    {
        var name = Marker();
        var response = await SendAsync(admin.Token, HttpMethod.Post, "/admin/skill-categories", new { nameTg = name, nameRu = name, nameEn = name, sortOrder = 90 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> SkillAsync(StaffPerson admin, Guid categoryId, string nameEn, bool active = true, string[]? synonyms = null)
    {
        nameEn = nameEn + " " + Marker();   // имена навыков в общей базе не повторяются (другие тесты проверяют уникальность по всему каталогу)
        var created = await SendAsync(admin.Token, HttpMethod.Post, "/admin/skills", new { categoryId, nameTg = nameEn, nameRu = nameEn, nameEn, synonyms });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        if (!active)
        {
            var put = await SendAsync(admin.Token, HttpMethod.Put, $"/admin/skills/{id}", new { categoryId, nameTg = nameEn, nameRu = nameEn, nameEn, synonyms, isActive = false });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        return id;
    }

    private async Task<(User User, string Token, Guid SuggestionId)> SuggestAsync(Guid categoryId, string name)
    {
        var (user, token) = await StaffActor.PersonalAsync(_factory);
        var response = await SendAsync(token, HttpMethod.Post, "/skills/suggestions", new { categoryId, name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (user, token, (await JsonAsync(response)).GetProperty("id").GetGuid());
    }

    private async Task<List<JsonElement>> NotificationsAsync(string token, string type)
    {
        var items = (await JsonAsync(await SendAsync(token, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return items.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    private Task<SkillSuggestion> RowAsync(Guid id) => _factory.QueryAsync(db => db.SkillSuggestions.AsNoTracking().SingleAsync(s => s.Id == id));

    private Task<int> AuditCountAsync(string action, Guid target, Guid actor) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == action && a.TargetId == target && a.ActorId == actor));

    private Task<HttpResponseMessage> ApproveAsync(StaffPerson admin, Guid id, string name, string[]? synonyms = null, Guid? categoryId = null) =>
        SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/approve", new { nameTg = name + " tg", nameRu = name + " ru", nameEn = name + " en", synonyms, categoryId });

    // ======================= права и очередь =======================

    [Fact]
    public async Task Only_active_admins_and_superadmins_work_with_the_suggestions()
    {
        var admin = await AdminAsync();
        var superAdmin = await AdminAsync(UserRole.SuperAdmin);
        var moderator = await AdminAsync(UserRole.Moderator);
        var (_, personal) = await StaffActor.PersonalAsync(_factory);
        var removed = await AdminAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == removed.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Status, UserStatus.Blocked));
            return 0;
        });
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/admin/skill-suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(personal, HttpMethod.Get, "/admin/skill-suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Get, "/admin/skill-suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/reject", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Get, "/admin/skill-suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(superAdmin.Token, HttpMethod.Get, "/admin/skill-suggestions")).StatusCode);
        Assert.Equal("invalid_paging", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Get, "/admin/skill-suggestions?pageSize=500")));
        // Снятый админ с живым токеном не работает ни с предложениями, ни с каталогом.
        Assert.Equal("admin_required", await CodeAsync(await SendAsync(removed.Token, HttpMethod.Get, "/admin/skill-suggestions")));
        Assert.Equal("admin_required", await CodeAsync(await SendAsync(removed.Token, HttpMethod.Post, "/admin/skill-categories", new { nameTg = "x", nameRu = "x", nameEn = Marker(), sortOrder = 1 })));
    }

    [Fact]
    public async Task The_queue_shows_the_author_the_category_the_same_name_count_and_similar_skills()
    {
        var admin = await AdminAsync();
        var category = await CategoryAsync(admin);
        var similar = await SkillAsync(admin, category, "Python 3");
        await SkillAsync(admin, category, "Cooking");
        var (first, _, suggestionId) = await SuggestAsync(category, "python");
        await SuggestAsync(category, "PYTHON");   // другой автор, то же название

        var list = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/skill-suggestions?categoryId={category}"));

        Assert.Equal(2, list.GetProperty("totalCount").GetInt32());
        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == suggestionId);
        Assert.Equal(first.Id, item.GetProperty("userId").GetGuid());
        Assert.Equal(first.Name, item.GetProperty("userName").GetString());
        Assert.Equal("Pending", item.GetProperty("status").GetString());
        Assert.Equal("python", item.GetProperty("name").GetString());
        Assert.Equal(2, item.GetProperty("sameNameCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("resolvedAt").ValueKind);
        Assert.Equal(similar, Assert.Single(item.GetProperty("similar").EnumerateArray()).GetProperty("id").GetGuid());
        var approvedList = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/skill-suggestions?categoryId={category}&status=Approved"));
        Assert.Equal(0, approvedList.GetProperty("totalCount").GetInt32());
    }

    // ======================= одобрение =======================

    [Fact]
    public async Task Approving_creates_the_skill_tells_the_author_and_merges_the_same_name_suggestions_of_others()
    {
        var admin = await AdminAsync();
        var category = await CategoryAsync(admin);
        var otherCategory = await CategoryAsync(admin);
        var (author, authorToken, id) = await SuggestAsync(category, "Rust");
        var (_, secondToken, secondId) = await SuggestAsync(category, "rust");              // тот же навык другим автором
        var (_, elsewhereToken, elsewhereId) = await SuggestAsync(otherCategory, "Rust");   // другая категория: не трогаем
        var name = Marker();

        var response = await ApproveAsync(admin, id, name, ["раст", "rust-lang"]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("Approved", dto.GetProperty("status").GetString());
        var skillId = dto.GetProperty("resolvedSkillId").GetGuid();
        Assert.Equal(admin.Id, dto.GetProperty("resolvedById").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, dto.GetProperty("resolvedAt").ValueKind);
        var skill = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/skills/{skillId}"));
        Assert.Equal(category, skill.GetProperty("categoryId").GetGuid());
        Assert.Equal(name + " tg", skill.GetProperty("nameTg").GetString());
        Assert.Equal(name + " en", skill.GetProperty("nameEn").GetString());
        Assert.Equal(["раст", "rust-lang"], skill.GetProperty("synonyms").EnumerateArray().Select(x => x.GetString()));
        Assert.True(skill.GetProperty("isActive").GetBoolean());
        var catalog = await JsonAsync(await SendAsync(authorToken, HttpMethod.Get, $"/skills?category={category}"));
        Assert.Contains(catalog.GetProperty("items").EnumerateArray(), s => s.GetProperty("id").GetGuid() == skillId);   // навык виден пользователям

        var note = Assert.Single(await NotificationsAsync(authorToken, "SkillSuggestionApproved")).GetProperty("payload");
        Assert.Equal(id, note.GetProperty("suggestionId").GetGuid());
        Assert.Equal("Rust", note.GetProperty("name").GetString());
        Assert.Equal(skillId, note.GetProperty("skillId").GetGuid());
        var second = await RowAsync(secondId);
        Assert.Equal(SuggestionStatus.Merged, second.Status);
        Assert.Equal(skillId, second.ResolvedSkillId);
        Assert.Single(await NotificationsAsync(secondToken, "SkillSuggestionMerged"));
        Assert.Equal(SuggestionStatus.Pending, (await RowAsync(elsewhereId)).Status);
        Assert.Empty(await NotificationsAsync(elsewhereToken, "SkillSuggestionMerged"));
        Assert.Equal(1, await AuditCountAsync("SkillSuggestionApproved", id, admin.Id));
        Assert.Equal(1, await AuditCountAsync("SkillSuggestionMerged", secondId, admin.Id));
        Assert.Equal(author.Id, (await RowAsync(id)).UserId);
    }

    [Fact]
    public async Task Approving_can_put_the_skill_into_another_category()
    {
        var admin = await AdminAsync();
        var category = await CategoryAsync(admin);
        var target = await CategoryAsync(admin);
        var (_, _, id) = await SuggestAsync(category, "Хореография");

        var response = await ApproveAsync(admin, id, Marker(), categoryId: target);

        var skillId = (await JsonAsync(response)).GetProperty("resolvedSkillId").GetGuid();
        Assert.Equal(target, (await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/skills/{skillId}"))).GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task Approving_is_refused_without_names_for_a_taken_name_an_unknown_category_and_twice()
    {
        var admin = await AdminAsync();
        var category = await CategoryAsync(admin);
        var takenId = await SkillAsync(admin, category, "Taken skill");
        var takenName = (await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/skills/{takenId}"))).GetProperty("nameEn").GetString()!;
        var (_, _, id) = await SuggestAsync(category, "Новый навык");

        var noNames = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/approve", new { nameTg = "a", nameRu = "", nameEn = "c" });
        var taken = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/approve", new { nameTg = "a", nameRu = "b", nameEn = takenName.ToUpperInvariant() });
        var unknownCategory = await ApproveAsync(admin, id, Marker(), categoryId: Guid.NewGuid());
        var unknown = await ApproveAsync(admin, Guid.NewGuid(), Marker());

        Assert.Equal("name_required", await CodeAsync(noNames));
        Assert.Equal("skill_name_taken", await CodeAsync(taken));   // регистр не важен
        Assert.Equal("category_not_found", await CodeAsync(unknownCategory));
        Assert.Equal("suggestion_not_found", await CodeAsync(unknown));
        Assert.Equal(SuggestionStatus.Pending, (await RowAsync(id)).Status);   // ошибки ничего не меняют
        Assert.Equal(HttpStatusCode.OK, (await ApproveAsync(admin, id, Marker())).StatusCode);
        var again = await ApproveAsync(admin, id, Marker());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(again));
    }

    // ======================= объединение =======================

    [Fact]
    public async Task Merging_links_the_existing_skill_adds_a_synonym_and_tells_the_author()
    {
        var admin = await AdminAsync();
        var category = await CategoryAsync(admin);
        var skillId = await SkillAsync(admin, category, "JavaScript", synonyms: ["js"]);
        var (_, token, id) = await SuggestAsync(category, "Джаваскрипт");
        var (_, secondToken, secondId) = await SuggestAsync(category, "джаваскрипт");

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/merge", new { skillId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("Merged", dto.GetProperty("status").GetString());
        Assert.Equal(skillId, dto.GetProperty("resolvedSkillId").GetGuid());
        var skill = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/skills/{skillId}"));
        Assert.Equal(["js", "джаваскрипт"], skill.GetProperty("synonyms").EnumerateArray().Select(x => x.GetString()));
        var note = Assert.Single(await NotificationsAsync(token, "SkillSuggestionMerged")).GetProperty("payload");
        Assert.Equal(skillId, note.GetProperty("skillId").GetGuid());
        Assert.Equal(SuggestionStatus.Merged, (await RowAsync(secondId)).Status);
        Assert.Single(await NotificationsAsync(secondToken, "SkillSuggestionMerged"));
        Assert.Equal(1, await AuditCountAsync("SkillSuggestionMerged", id, admin.Id));
    }

    [Fact]
    public async Task Merging_without_the_synonym_leaves_the_skill_as_it_was()
    {
        var admin = await AdminAsync();
        var category = await CategoryAsync(admin);
        var skillId = await SkillAsync(admin, category, "Photoshop");
        var (_, _, id) = await SuggestAsync(category, "Фотошоп");

        await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/merge", new { skillId, addAsSynonym = false });

        var skill = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/skills/{skillId}"));
        Assert.Empty(skill.GetProperty("synonyms").EnumerateArray());
    }

    [Fact]
    public async Task Merging_is_refused_for_an_unknown_or_switched_off_skill_and_for_a_resolved_suggestion()
    {
        var admin = await AdminAsync();
        var category = await CategoryAsync(admin);
        var off = await SkillAsync(admin, category, "Old skill", active: false);
        var on = await SkillAsync(admin, category, "New skill");
        var (_, _, id) = await SuggestAsync(category, "Что-то");

        var inactive = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/merge", new { skillId = off });
        var unknownSkill = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/merge", new { skillId = Guid.NewGuid() });
        var unknown = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{Guid.NewGuid()}/merge", new { skillId = on });

        Assert.Equal("skill_not_active", await CodeAsync(inactive));
        Assert.Equal("skill_not_found", await CodeAsync(unknownSkill));
        Assert.Equal("suggestion_not_found", await CodeAsync(unknown));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/merge", new { skillId = on })).StatusCode);
        var again = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/merge", new { skillId = on });
        Assert.Equal("invalid_state_transition", await CodeAsync(again));
    }

    // ======================= отказ =======================

    [Fact]
    public async Task Rejecting_tells_the_author_the_reason_and_lets_them_suggest_again()
    {
        var admin = await AdminAsync();
        var category = await CategoryAsync(admin);
        var (_, token, id) = await SuggestAsync(category, "Непонятное");

        var response = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/reject", new { reason = "  Слишком общее название.  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("Rejected", dto.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("resolvedSkillId").ValueKind);
        var note = Assert.Single(await NotificationsAsync(token, "SkillSuggestionRejected")).GetProperty("payload");
        Assert.Equal("Слишком общее название.", note.GetProperty("reason").GetString());
        Assert.Equal("Непонятное", note.GetProperty("name").GetString());
        Assert.Equal(1, await AuditCountAsync("SkillSuggestionRejected", id, admin.Id));
        var again = await SendAsync(token, HttpMethod.Post, "/skills/suggestions", new { categoryId = category, name = "Непонятное" });
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);   // отказ не блокирует новое предложение
    }

    [Fact]
    public async Task Rejecting_works_without_a_reason_and_only_for_this_suggestion()
    {
        var admin = await AdminAsync();
        var category = await CategoryAsync(admin);
        var (_, token, id) = await SuggestAsync(category, "Что-нибудь");
        var (_, _, otherId) = await SuggestAsync(category, "что-нибудь");

        var noBody = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/reject");
        var tooLong = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{otherId}/reject", new { reason = new string('я', 501) });

        Assert.Equal(HttpStatusCode.OK, noBody.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await NotificationsAsync(token, "SkillSuggestionRejected")).Single().GetProperty("payload").GetProperty("reason").ValueKind);
        Assert.Equal("reject_reason_too_long", await CodeAsync(tooLong));
        Assert.Equal(SuggestionStatus.Pending, (await RowAsync(otherId)).Status);   // чужое предложение с тем же названием остаётся
        var again = await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{id}/reject", new { });
        Assert.Equal("invalid_state_transition", await CodeAsync(again));
        Assert.Equal("suggestion_not_found", await CodeAsync(await SendAsync(admin.Token, HttpMethod.Post, $"/admin/skill-suggestions/{Guid.NewGuid()}/reject", new { })));
    }

    // ======================= каталог в аудите =======================

    [Fact]
    public async Task Every_catalog_change_by_an_admin_is_in_the_audit_log()
    {
        var admin = await AdminAsync();
        var category = await CategoryAsync(admin);
        var skillId = await SkillAsync(admin, category, "Audit skill");
        var name = Marker();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Put, $"/admin/skills/{skillId}", new { categoryId = category, nameTg = name, nameRu = name, nameEn = name, isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(admin.Token, HttpMethod.Delete, $"/admin/skills/{skillId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, HttpMethod.Put, $"/admin/skill-categories/{category}", new { nameTg = name, nameRu = name, nameEn = name, sortOrder = 91, isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(admin.Token, HttpMethod.Delete, $"/admin/skill-categories/{category}")).StatusCode);

        Assert.Equal(1, await AuditCountAsync("CategoryCreated", category, admin.Id));
        Assert.Equal(1, await AuditCountAsync("CategoryUpdated", category, admin.Id));
        Assert.Equal(1, await AuditCountAsync("CategoryDeleted", category, admin.Id));
        Assert.Equal(1, await AuditCountAsync("SkillCreated", skillId, admin.Id));
        Assert.Equal(1, await AuditCountAsync("SkillUpdated", skillId, admin.Id));
        Assert.Equal(1, await AuditCountAsync("SkillDeleted", skillId, admin.Id));
    }
}
