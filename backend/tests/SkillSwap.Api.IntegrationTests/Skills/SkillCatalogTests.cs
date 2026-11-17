using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence.Seed;

namespace SkillSwap.Api.IntegrationTests.Skills;

/// <summary>Каталог навыков (раздел 5.2 ТЗ): категории, поиск по названиям и синонимам, фильтр, страницы, скрытые записи.</summary>
[Collection(ApiCollection.Name)]
public class SkillCatalogTests : IAsyncLifetime
{
    private const int SeededSkills = 29;
    private readonly ApiFactory _factory;
    private readonly AuthApi _api;
    private string _token = string.Empty;

    public SkillCatalogTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    public async Task InitializeAsync()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        _token = (await _api.LoginSessionAsync(email)).AccessToken;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> GetAsync(string url, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1" + url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _token);
        return await _api.Client.SendAsync(request);
    }

    private async Task<JsonElement> JsonAsync(string url)
    {
        var response = await GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<List<string>> NamesEnAsync(string url) =>
        (await JsonAsync(url)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("nameEn").GetString()!).ToList();

    // ---------- доступ ----------

    [Theory]
    [InlineData("/skill-categories")]
    [InlineData("/skills")]
    public async Task Reading_the_catalog_requires_a_login(string url)
    {
        var anonymous = await _api.Client.GetAsync("/api/v1" + url);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ---------- категории ----------

    [Fact]
    public async Task The_three_starting_categories_come_in_order_with_names_in_all_languages()
    {
        // Другие тесты добавляют свои категории в общую БД (с большим sortOrder), поэтому смотрим на стартовые три.
        var seeded = new[] { SkillCatalogSeed.It, SkillCatalogSeed.Languages, SkillCatalogSeed.Design }.Select(g => g.ToString()).ToHashSet();
        var all = (await JsonAsync("/skill-categories")).EnumerateArray().ToList();
        var categories = all.Where(c => seeded.Contains(c.GetProperty("id").GetString()!)).ToList();
        Assert.Equal(all.Take(3).Select(c => c.GetProperty("id").GetString()), categories.Select(c => c.GetProperty("id").GetString()));   // они идут первыми

        Assert.Equal(["IT", "Foreign languages", "Design"], categories.Select(c => c.GetProperty("nameEn").GetString()));
        Assert.Equal("Иностранные языки", categories[1].GetProperty("nameRu").GetString());
        Assert.Equal("Забонҳои хориҷӣ", categories[1].GetProperty("nameTg").GetString());
        Assert.Equal(SkillCatalogSeed.It.ToString(), categories[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_switched_off_category_is_not_listed_and_hides_its_skills()
    {
        var marker = "Hidden-" + Guid.NewGuid().ToString("N");
        var categoryId = await _factory.QueryAsync(async db =>
        {
            var category = SkillCategory.Create(marker, marker, marker, 99);
            category.Update(marker, marker, marker, 99, isActive: false);
            db.SkillCategories.Add(category);
            db.Skills.Add(Skill.Create(category.Id, marker, marker, marker));
            await db.SaveChangesAsync();
            return category.Id;
        });

        var categories = (await JsonAsync("/skill-categories")).EnumerateArray().Select(c => c.GetProperty("id").GetString());
        Assert.DoesNotContain(categoryId.ToString(), categories);
        Assert.Empty(await NamesEnAsync($"/skills?q={marker}"));
    }

    // ---------- поиск ----------

    [Fact]
    public async Task Without_filters_the_whole_starting_catalog_is_returned_page_by_page()
    {
        // Другие тесты добавляют свои навыки в общую БД, поэтому всего может быть больше стартовых 29.
        var first = await JsonAsync("/skills?pageSize=10");
        var total = first.GetProperty("totalCount").GetInt32();
        Assert.True(total >= SeededSkills);
        Assert.Equal(10, first.GetProperty("items").GetArrayLength());

        var all = new List<string>();
        var pages = (total + 9) / 10;
        for (var page = 1; page <= pages; page++)
        {
            all.AddRange(await NamesEnAsync($"/skills?pageSize=10&page={page}"));
        }

        Assert.Equal(total, all.Count);
        Assert.Equal(total, all.Distinct().Count());   // страницы не пересекаются и ничего не теряют
        Assert.Empty(await NamesEnAsync($"/skills?pageSize=10&page={pages + 1}"));
        Assert.Contains("Python", all);
        Assert.Contains("Typography", all);
    }

    [Theory]
    [InlineData("Английский", "English")]        // русское название
    [InlineData("англисӣ", "English")]           // таджикское название
    [InlineData("english", "English")]           // английское название
    [InlineData("PYTHON", "Python")]             // регистр не важен
    [InlineData("  python  ", "Python")]         // пробелы по краям отбрасываются
    [InlineData("питон", "Python")]              // синоним на кириллице
    [InlineData("js", "JavaScript")]             // короткий синоним
    [InlineData("Си Шарп", "C#")]                // синоним с пробелом и другим регистром
    [InlineData("фотошоп", "Adobe Photoshop")]
    public async Task Search_finds_a_skill_by_name_in_any_language_or_by_synonym(string query, string expected)
    {
        var found = await NamesEnAsync($"/skills?q={Uri.EscapeDataString(query)}");

        Assert.Contains(expected, found);
    }

    [Fact]
    public async Task A_query_that_matches_nothing_gives_an_empty_page_not_an_error()
    {
        var result = await JsonAsync("/skills?q=" + Guid.NewGuid().ToString("N"));

        Assert.Equal(0, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, result.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    [InlineData("py%")]
    [InlineData("p_thon")]
    public async Task Wildcard_characters_in_the_query_are_taken_literally(string query)
    {
        Assert.Empty(await NamesEnAsync($"/skills?q={Uri.EscapeDataString(query)}"));
    }

    [Fact]
    public async Task A_name_with_special_characters_is_found_by_exactly_that_text()
    {
        var marker = "100%_" + Guid.NewGuid().ToString("N")[..8];
        await _factory.QueryAsync(async db =>
        {
            db.Skills.Add(Skill.Create(SkillCatalogSeed.It, marker, marker, marker));
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal([marker], await NamesEnAsync($"/skills?q={Uri.EscapeDataString(marker)}"));
    }

    [Fact]
    public async Task The_category_filter_limits_the_result()
    {
        var result = await JsonAsync($"/skills?category={SkillCatalogSeed.Languages}&pageSize=50");

        Assert.Equal(10, result.GetProperty("totalCount").GetInt32());
        Assert.All(result.GetProperty("items").EnumerateArray(),
            i => Assert.Equal(SkillCatalogSeed.Languages.ToString(), i.GetProperty("categoryId").GetString()));
    }

    [Fact]
    public async Task The_category_filter_combines_with_the_query()
    {
        // «java» есть и в «JavaScript», и в «Java» (IT), но не в языках.
        Assert.Empty(await NamesEnAsync($"/skills?category={SkillCatalogSeed.Languages}&q=java"));
        Assert.Contains("Java", await NamesEnAsync($"/skills?category={SkillCatalogSeed.It}&q=java"));
    }

    [Fact]
    public async Task An_unknown_category_gives_an_empty_page()
    {
        Assert.Empty(await NamesEnAsync($"/skills?category={Guid.NewGuid()}"));
    }

    [Fact]
    public async Task A_switched_off_skill_is_not_found()
    {
        var marker = "Off-" + Guid.NewGuid().ToString("N");
        await _factory.QueryAsync(async db =>
        {
            var off = Skill.Create(SkillCatalogSeed.It, marker + "-off", marker + "-off", marker + "-off");
            off.Update(SkillCatalogSeed.It, marker + "-off", marker + "-off", marker + "-off", null, isActive: false);
            db.Skills.AddRange(off, Skill.Create(SkillCatalogSeed.It, marker + "-on", marker + "-on", marker + "-on"));
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal([marker + "-on"], await NamesEnAsync($"/skills?q={marker}"));
    }

    [Theory]
    [InlineData("tg")]
    [InlineData("ru")]
    [InlineData("en")]
    [InlineData("RU")]
    public async Task Every_sort_language_returns_the_whole_catalog_without_duplicates(string lang)
    {
        // Берём только страницы стартового набора: категория «Иностранные языки» (в неё тесты ничего не добавляют).
        var names = await NamesEnAsync($"/skills?lang={lang}&pageSize=50&category={SkillCatalogSeed.Languages}");

        Assert.Equal(10, names.Count);
        Assert.Equal(10, names.Distinct().Count());
    }

    // ---------- плохие запросы ----------

    [Theory]
    [InlineData("page=0", "invalid_paging")]
    [InlineData("page=-1", "invalid_paging")]
    [InlineData("pageSize=0", "invalid_paging")]
    [InlineData("pageSize=51", "invalid_paging")]
    [InlineData("lang=de", "unsupported_language")]
    public async Task Bad_parameters_get_a_400_with_a_machine_readable_code(string query, string code)
    {
        var response = await GetAsync("/skills?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_too_long_query_is_refused_and_the_maximum_length_is_fine()
    {
        var tooLong = await GetAsync("/skills?q=" + new string('a', 101));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("query_too_long", await AuthApi.ReadCodeAsync(tooLong));

        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/skills?q=" + new string('a', 100))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync("/skills?pageSize=50")).StatusCode);
    }

    [Fact]
    public async Task A_malformed_category_id_is_a_400_not_a_server_error()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync("/skills?category=not-a-guid")).StatusCode);
    }

    // ---------- сам каталог ----------

    [Fact]
    public async Task The_starting_catalog_is_complete_and_the_same_skill_cannot_be_added_twice_to_a_category()
    {
        var (categories, skills, incomplete) = await _factory.QueryAsync(async db => (
            await db.SkillCategories.CountAsync(c => c.IsActive),
            await db.Skills.CountAsync(s => s.Category!.IsActive && s.IsActive),
            await db.Skills.CountAsync(s => s.NameTg == "" || s.NameRu == "" || s.NameEn == "")));

        Assert.True(categories >= 3);
        Assert.True(skills >= SeededSkills);
        Assert.Equal(0, incomplete);

        await Assert.ThrowsAsync<DbUpdateException>(() => _factory.QueryAsync(async db =>
        {
            db.Skills.Add(Skill.Create(SkillCatalogSeed.It, "x", "x", "Python"));   // «Python» в IT уже есть
            await db.SaveChangesAsync();
            return 0;
        }));
    }
}
