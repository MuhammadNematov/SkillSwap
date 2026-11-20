using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence.Seed;

namespace SkillSwap.Api.IntegrationTests.Skills;

/// <summary>Правка каталога админом (раздел 5.2 ТЗ): доступ по роли, создание, замена, выключение, удаление, конфликты.</summary>
[Collection(ApiCollection.Name)]
public class AdminSkillCatalogTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private readonly string _admin;

    public AdminSkillCatalogTests(ApiFactory factory)
    {
        _factory = factory;
        _client = new AuthApi(factory).Client;
        _admin = ModerationTestData.StaffAsync(factory, UserRole.Admin).GetAwaiter().GetResult().Token;   // настоящий админ: сервис проверяет статус по базе
    }

    private static string Marker() => "T" + Guid.NewGuid().ToString("N")[..10];

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

    private Task<HttpResponseMessage> AdminAsync(HttpMethod method, string url, object? body = null) =>
        SendAsync(_admin, method, url, body);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<JsonElement> CreateCategoryAsync(string? nameEn = null, int sortOrder = 50)
    {
        var name = nameEn ?? Marker();
        var response = await AdminAsync(HttpMethod.Post, "/admin/skill-categories", new { nameTg = name, nameRu = name, nameEn = name, sortOrder });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await BodyAsync(response);
    }

    private async Task<JsonElement> CreateSkillAsync(Guid categoryId, string? nameEn = null, string[]? synonyms = null)
    {
        var name = nameEn ?? Marker();
        var response = await AdminAsync(HttpMethod.Post, "/admin/skills", new { categoryId, nameTg = name, nameRu = name, nameEn = name, synonyms });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await BodyAsync(response);
    }

    private static Guid Id(JsonElement element) => element.GetProperty("id").GetGuid();

    // ---------- доступ по роли ----------

    [Fact]
    public async Task Without_a_token_the_admin_catalog_answers_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/admin/skill-categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/admin/skills")).StatusCode);
    }

    [Theory]
    [InlineData(UserRole.User)]
    [InlineData(UserRole.Moderator)]
    public async Task Users_and_moderators_cannot_touch_the_catalog(UserRole role)
    {
        var token = TestTokens.For(_factory, role);
        var someId = SkillCatalogSeed.SkillId(1);
        var body = new { categoryId = SkillCatalogSeed.It, nameTg = "x", nameRu = "x", nameEn = Marker() };

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Get, "/admin/skill-categories")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Get, "/admin/skills")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Post, "/admin/skills", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Put, $"/admin/skills/{someId}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Delete, $"/admin/skills/{someId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(token, HttpMethod.Post, "/admin/skill-categories", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(token, HttpMethod.Delete, $"/admin/skill-categories/{SkillCatalogSeed.It}")).StatusCode);
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.SuperAdmin)]
    public async Task Admins_and_superadmins_can_read_it(UserRole role)
    {
        var response = await SendAsync((await ModerationTestData.StaffAsync(_factory, role)).Token, HttpMethod.Get, "/admin/skill-categories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- категории ----------

    [Fact]
    public async Task A_category_can_be_created_read_replaced_and_deleted()
    {
        var name = Marker();
        var created = await AdminAsync(HttpMethod.Post, "/admin/skill-categories",
            new { nameTg = "  Тоҷ ", nameRu = "Рус", nameEn = $" {name} ", sortOrder = 40 });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await BodyAsync(created);
        Assert.EndsWith($"/api/v1/admin/skill-categories/{Id(dto)}", created.Headers.Location!.ToString());
        Assert.Equal("Тоҷ", dto.GetProperty("nameTg").GetString());      // пробелы по краям убраны
        Assert.Equal(name, dto.GetProperty("nameEn").GetString());
        Assert.True(dto.GetProperty("isActive").GetBoolean());
        Assert.Equal(0, dto.GetProperty("skillCount").GetInt32());

        var id = Id(dto);
        var put = await AdminAsync(HttpMethod.Put, $"/admin/skill-categories/{id}",
            new { nameTg = "A", nameRu = "B", nameEn = name + "-2", sortOrder = 41, isActive = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(name + "-2", (await BodyAsync(put)).GetProperty("nameEn").GetString());

        Assert.Equal(HttpStatusCode.OK, (await AdminAsync(HttpMethod.Get, $"/admin/skill-categories/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AdminAsync(HttpMethod.Delete, $"/admin/skill-categories/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(HttpMethod.Get, $"/admin/skill-categories/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_category_name_is_unique_regardless_of_letter_case()
    {
        var name = Marker();
        await CreateCategoryAsync(name);

        var duplicate = await AdminAsync(HttpMethod.Post, "/admin/skill-categories",
            new { nameTg = "x", nameRu = "x", nameEn = name.ToLowerInvariant(), sortOrder = 1 });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("category_name_taken", await AuthApi.ReadCodeAsync(duplicate));
    }

    [Fact]
    public async Task Replacing_a_category_with_its_own_name_is_not_a_conflict()
    {
        var category = await CreateCategoryAsync();

        var put = await AdminAsync(HttpMethod.Put, $"/admin/skill-categories/{Id(category)}",
            new { nameTg = "new", nameRu = "new", nameEn = category.GetProperty("nameEn").GetString(), sortOrder = 5, isActive = true });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    [Fact]
    public async Task Switching_a_category_off_hides_it_and_its_skills_from_users_but_not_from_the_admin()
    {
        var category = await CreateCategoryAsync();
        var skill = await CreateSkillAsync(Id(category));
        var skillName = skill.GetProperty("nameEn").GetString();
        var user = (await new AuthApi(_factory).LoginSessionAsync(await new AuthApi(_factory).RegisterConfirmedUserAsync())).AccessToken;

        var off = await AdminAsync(HttpMethod.Put, $"/admin/skill-categories/{Id(category)}",
            new { nameTg = "x", nameRu = "x", nameEn = category.GetProperty("nameEn").GetString(), sortOrder = 1, isActive = false });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        var seen = await BodyAsync(await SendAsync(user, HttpMethod.Get, $"/skills?q={skillName}"));
        Assert.Equal(0, seen.GetProperty("totalCount").GetInt32());
        var adminSees = await BodyAsync(await AdminAsync(HttpMethod.Get, $"/admin/skills?q={skillName}"));
        Assert.Equal(1, adminSees.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_category_with_skills_cannot_be_deleted()
    {
        var category = await CreateCategoryAsync();
        await CreateSkillAsync(Id(category));

        var delete = await AdminAsync(HttpMethod.Delete, $"/admin/skill-categories/{Id(category)}");

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("category_not_empty", await AuthApi.ReadCodeAsync(delete));
        Assert.Equal(1, (await BodyAsync(await AdminAsync(HttpMethod.Get, $"/admin/skill-categories/{Id(category)}"))).GetProperty("skillCount").GetInt32());
    }

    [Theory]
    [InlineData("", "ru", "en", 1)]
    [InlineData("tg", "   ", "en", 1)]
    [InlineData("tg", "ru", "", 1)]
    [InlineData("tg", "ru", "en", -1)]
    [InlineData("tg", "ru", "en", 10001)]
    public async Task Invalid_category_data_is_a_400_with_field_errors(string tg, string ru, string en, int sort)
    {
        var response = await AdminAsync(HttpMethod.Post, "/admin/skill-categories", new { nameTg = tg, nameRu = ru, nameEn = en, sortOrder = sort });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_name_over_100_characters_is_refused_and_exactly_100_is_fine()
    {
        var tooLong = await AdminAsync(HttpMethod.Post, "/admin/skill-categories",
            new { nameTg = "x", nameRu = "x", nameEn = new string('a', 101), sortOrder = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var ok = await AdminAsync(HttpMethod.Post, "/admin/skill-categories",
            new { nameTg = "x", nameRu = "x", nameEn = Marker().PadRight(100, 'b'), sortOrder = 1 });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task A_full_replacement_without_isactive_is_refused_instead_of_silently_switching_off()
    {
        var category = await CreateCategoryAsync();

        var put = await AdminAsync(HttpMethod.Put, $"/admin/skill-categories/{Id(category)}",
            new { nameTg = "x", nameRu = "x", nameEn = Marker(), sortOrder = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.True((await BodyAsync(await AdminAsync(HttpMethod.Get, $"/admin/skill-categories/{Id(category)}"))).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task The_admin_category_list_shows_switched_off_ones_with_skill_counts()
    {
        var category = await CreateCategoryAsync();
        await CreateSkillAsync(Id(category));
        await CreateSkillAsync(Id(category));

        var list = (await BodyAsync(await AdminAsync(HttpMethod.Get, "/admin/skill-categories"))).EnumerateArray().ToList();

        Assert.Equal(2, list.Single(c => Id(c) == Id(category)).GetProperty("skillCount").GetInt32());
        // В IT стартовых навыков 11; другие тесты могли добавить свои, поэтому «не меньше».
        Assert.True(list.Single(c => Id(c) == SkillCatalogSeed.It).GetProperty("skillCount").GetInt32() >= 11);
    }

    // ---------- навыки ----------

    [Fact]
    public async Task A_skill_is_created_with_normalized_synonyms_and_becomes_searchable_by_them()
    {
        var category = await CreateCategoryAsync();
        var marker = Marker().ToLowerInvariant();

        var skill = await CreateSkillAsync(Id(category), synonyms: [$"  {marker.ToUpperInvariant()} ", marker, "", "   ", "Другое"]);

        Assert.Equal([marker, "другое"], skill.GetProperty("synonyms").EnumerateArray().Select(s => s.GetString()));
        var user = (await new AuthApi(_factory).LoginSessionAsync(await new AuthApi(_factory).RegisterConfirmedUserAsync())).AccessToken;
        var found = await BodyAsync(await SendAsync(user, HttpMethod.Get, $"/skills?q={marker}"));
        Assert.Equal(Id(skill), found.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_skill_for_a_missing_category_is_a_404()
    {
        var name = Marker();
        var response = await AdminAsync(HttpMethod.Post, "/admin/skills",
            new { categoryId = Guid.NewGuid(), nameTg = name, nameRu = name, nameEn = name });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("category_not_found", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_skill_name_is_unique_within_a_category_but_may_repeat_in_another()
    {
        var first = await CreateCategoryAsync();
        var second = await CreateCategoryAsync();
        var name = Marker();
        await CreateSkillAsync(Id(first), name);

        var sameCategory = await AdminAsync(HttpMethod.Post, "/admin/skills",
            new { categoryId = Id(first), nameTg = "x", nameRu = "x", nameEn = name.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.Conflict, sameCategory.StatusCode);
        Assert.Equal("skill_name_taken", await AuthApi.ReadCodeAsync(sameCategory));

        await CreateSkillAsync(Id(second), name);   // другая категория — можно
    }

    [Fact]
    public async Task Two_admins_adding_the_same_skill_at_once_get_one_success_and_one_conflict()
    {
        var category = await CreateCategoryAsync();
        var name = Marker();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AdminAsync(HttpMethod.Post, "/admin/skills",
            new { categoryId = Id(category), nameTg = name, nameRu = name, nameEn = name })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));   // а не 500
    }

    [Fact]
    public async Task A_skill_can_be_replaced_moved_switched_off_and_deleted()
    {
        var first = await CreateCategoryAsync();
        var second = await CreateCategoryAsync();
        var skill = await CreateSkillAsync(Id(first), synonyms: ["old"]);
        var name = Marker();

        var put = await AdminAsync(HttpMethod.Put, $"/admin/skills/{Id(skill)}",
            new { categoryId = Id(second), nameTg = "a", nameRu = "b", nameEn = name, synonyms = new[] { "new" }, isActive = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await BodyAsync(put);
        Assert.Equal(Id(second), updated.GetProperty("categoryId").GetGuid());
        Assert.Equal(["new"], updated.GetProperty("synonyms").EnumerateArray().Select(s => s.GetString()));

        var off = await AdminAsync(HttpMethod.Put, $"/admin/skills/{Id(skill)}",
            new { categoryId = Id(second), nameTg = "a", nameRu = "b", nameEn = name, synonyms = new[] { "new" }, isActive = false });
        Assert.False((await BodyAsync(off)).GetProperty("isActive").GetBoolean());

        var user = (await new AuthApi(_factory).LoginSessionAsync(await new AuthApi(_factory).RegisterConfirmedUserAsync())).AccessToken;
        Assert.Equal(0, (await BodyAsync(await SendAsync(user, HttpMethod.Get, $"/skills?q={name}"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await BodyAsync(await AdminAsync(HttpMethod.Get, $"/admin/skills?q={name}"))).GetProperty("totalCount").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await AdminAsync(HttpMethod.Delete, $"/admin/skills/{Id(skill)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(HttpMethod.Get, $"/admin/skills/{Id(skill)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(HttpMethod.Delete, $"/admin/skills/{Id(skill)}")).StatusCode);
    }

    [Fact]
    public async Task Renaming_a_skill_to_another_skills_name_in_the_same_category_is_a_conflict_but_keeping_its_own_is_fine()
    {
        var category = await CreateCategoryAsync();
        var a = await CreateSkillAsync(Id(category));
        var b = await CreateSkillAsync(Id(category));

        var clash = await AdminAsync(HttpMethod.Put, $"/admin/skills/{Id(b)}",
            new { categoryId = Id(category), nameTg = "x", nameRu = "x", nameEn = a.GetProperty("nameEn").GetString(), isActive = true });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);

        var own = await AdminAsync(HttpMethod.Put, $"/admin/skills/{Id(b)}",
            new { categoryId = Id(category), nameTg = "x", nameRu = "x", nameEn = b.GetProperty("nameEn").GetString(), isActive = true });
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task Moving_a_skill_to_a_missing_category_is_a_404_and_changes_nothing()
    {
        var category = await CreateCategoryAsync();
        var skill = await CreateSkillAsync(Id(category));

        var put = await AdminAsync(HttpMethod.Put, $"/admin/skills/{Id(skill)}",
            new { categoryId = Guid.NewGuid(), nameTg = "x", nameRu = "x", nameEn = "x", isActive = true });

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(Id(category), (await BodyAsync(await AdminAsync(HttpMethod.Get, $"/admin/skills/{Id(skill)}"))).GetProperty("categoryId").GetGuid());
    }

    [Fact]
    public async Task Synonym_limits_are_enforced()
    {
        var category = await CreateCategoryAsync();
        var name = Marker();

        var tooMany = await AdminAsync(HttpMethod.Post, "/admin/skills", new
        {
            categoryId = Id(category), nameTg = name, nameRu = name, nameEn = name,
            synonyms = Enumerable.Range(0, 21).Select(i => "s" + i).ToArray(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);

        var tooLong = await AdminAsync(HttpMethod.Post, "/admin/skills", new
        {
            categoryId = Id(category), nameTg = name, nameRu = name, nameEn = name, synonyms = new[] { new string('x', 61) },
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        // Ровно 20 разных (повторы и пустые не считаются) — можно.
        var ok = await AdminAsync(HttpMethod.Post, "/admin/skills", new
        {
            categoryId = Id(category), nameTg = name, nameRu = name, nameEn = name,
            synonyms = Enumerable.Range(0, 20).Select(i => "s" + i).Concat(["S0", "", " "]).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Unknown_ids_give_404_not_500()
    {
        var id = Guid.NewGuid();
        var body = new { categoryId = SkillCatalogSeed.It, nameTg = "x", nameRu = "x", nameEn = "x", isActive = true };

        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(HttpMethod.Get, $"/admin/skills/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(HttpMethod.Put, $"/admin/skills/{id}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(HttpMethod.Put, $"/admin/skill-categories/{id}",
            new { nameTg = "x", nameRu = "x", nameEn = "x", sortOrder = 1, isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AdminAsync(HttpMethod.Delete, $"/admin/skill-categories/{id}")).StatusCode);
    }

    [Fact]
    public async Task The_admin_skill_list_filters_by_activity_category_and_query_and_pages_correctly()
    {
        var category = await CreateCategoryAsync();
        var prefix = Marker();
        for (var i = 0; i < 5; i++)
        {
            await CreateSkillAsync(Id(category), $"{prefix}-{i}");
        }

        var skill = await CreateSkillAsync(Id(category), $"{prefix}-off");
        await AdminAsync(HttpMethod.Put, $"/admin/skills/{Id(skill)}",
            new { categoryId = Id(category), nameTg = "x", nameRu = "x", nameEn = $"{prefix}-off", isActive = false });

        var all = await BodyAsync(await AdminAsync(HttpMethod.Get, $"/admin/skills?category={Id(category)}&pageSize=4"));
        Assert.Equal(6, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, all.GetProperty("items").GetArrayLength());

        var page2 = await BodyAsync(await AdminAsync(HttpMethod.Get, $"/admin/skills?category={Id(category)}&pageSize=4&page=2"));
        Assert.Equal(2, page2.GetProperty("items").GetArrayLength());

        var activeOnly = await BodyAsync(await AdminAsync(HttpMethod.Get, $"/admin/skills?category={Id(category)}&isActive=true"));
        Assert.Equal(5, activeOnly.GetProperty("totalCount").GetInt32());
        var inactiveOnly = await BodyAsync(await AdminAsync(HttpMethod.Get, $"/admin/skills?category={Id(category)}&isActive=false"));
        Assert.Equal(1, inactiveOnly.GetProperty("totalCount").GetInt32());

        var byQuery = await BodyAsync(await AdminAsync(HttpMethod.Get, $"/admin/skills?q={prefix}-3"));
        Assert.Equal(1, byQuery.GetProperty("totalCount").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await AdminAsync(HttpMethod.Get, "/admin/skills?pageSize=51")).StatusCode);
    }
}
