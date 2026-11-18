using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence.Seed;

namespace SkillSwap.Api.IntegrationTests.Skills;

/// <summary>Предложение нового навыка (раздел 5.2 ТЗ): POST /skills/suggestions.</summary>
[Collection(ApiCollection.Name)]
public class SkillSuggestionTests
{
    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public SkillSuggestionTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    private sealed record Person(string Token, Guid Id);

    private async Task<Person> NewPersonAsync()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var token = (await _api.LoginSessionAsync(email)).AccessToken;
        var me = await (await SendAsync(token, HttpMethod.Get, "/me")).Content.ReadFromJsonAsync<JsonElement>();
        return new Person(token, me.GetProperty("id").GetGuid());
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

        return await _api.Client.SendAsync(request);
    }

    private static string Unique(string prefix = "Нав") => prefix + Guid.NewGuid().ToString("N")[..10];

    private Task<HttpResponseMessage> SuggestAsync(Person p, string name, Guid? category = null) =>
        SendAsync(p.Token, HttpMethod.Post, "/skills/suggestions", new { categoryId = category ?? SkillCatalogSeed.It, name });

    private static async Task<string?> CodeAsync(HttpResponseMessage r) => await AuthApi.ReadCodeAsync(r);

    // ---------- доступ и результат ----------

    [Fact]
    public async Task A_login_is_required()
    {
        var response = await SendAsync(null, HttpMethod.Post, "/skills/suggestions", new { categoryId = SkillCatalogSeed.It, name = "Rust" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_suggestion_is_created_as_pending_with_a_tidied_name()
    {
        var person = await NewPersonAsync();
        var name = Unique("Rust");

        var response = await SuggestAsync(person, $"   {name}    language  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"{name} language", dto.GetProperty("name").GetString());   // лишние пробелы убраны
        Assert.Equal("Pending", dto.GetProperty("status").GetString());
        Assert.Equal(SkillCatalogSeed.It, dto.GetProperty("categoryId").GetGuid());
        Assert.NotEqual(default, dto.GetProperty("createdAt").GetDateTime());

        var row = await _factory.QueryAsync(db => db.SkillSuggestions.SingleAsync(s => s.Id == dto.GetProperty("id").GetGuid()));
        Assert.Equal(person.Id, row.UserId);
        Assert.Equal($"{name} language".ToLowerInvariant(), row.NormalizedName);
        Assert.Equal(SuggestionStatus.Pending, row.Status);
        Assert.Null(row.ResolvedSkillId);
    }

    // ---------- проверка названия ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("12345")]
    [InlineData("!!! ???")]
    [InlineData("ab\u0001cd")]
    public async Task A_bad_name_is_a_400(string name)
    {
        var person = await NewPersonAsync();

        var response = await SuggestAsync(person, name);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeAsync(response));
    }

    [Fact]
    public async Task A_missing_name_or_category_is_a_400()
    {
        var person = await NewPersonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(person.Token, HttpMethod.Post, "/skills/suggestions", new { categoryId = SkillCatalogSeed.It })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(person.Token, HttpMethod.Post, "/skills/suggestions", new { name = "Rust" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SuggestAsync(person, "Rust", Guid.Empty)).StatusCode);
    }

    [Fact]
    public async Task The_name_length_limits_are_two_and_one_hundred_characters()
    {
        var person = await NewPersonAsync();

        Assert.Equal(HttpStatusCode.Created, (await SuggestAsync(person, Unique("Go")[..2] + "")).StatusCode);   // ровно 2 символа
        Assert.Equal(HttpStatusCode.Created, (await SuggestAsync(person, Unique("Я").PadRight(100, 'я'))).StatusCode);   // ровно 100
        Assert.Equal(HttpStatusCode.BadRequest, (await SuggestAsync(person, Unique("Я").PadRight(101, 'я'))).StatusCode);
    }

    // ---------- категория ----------

    [Fact]
    public async Task An_unknown_or_switched_off_category_is_a_400_unknown_category()
    {
        var person = await NewPersonAsync();
        var marker = Unique("Off");
        var offCategory = await _factory.QueryAsync(async db =>
        {
            var category = SkillCategory.Create(marker, marker, marker, 95);
            category.Update(marker, marker, marker, 95, isActive: false);
            db.SkillCategories.Add(category);
            await db.SaveChangesAsync();
            return category.Id;
        });

        foreach (var category in new[] { Guid.NewGuid(), offCategory })
        {
            var response = await SuggestAsync(person, Unique(), category);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("unknown_category", await CodeAsync(response));
        }
    }

    // ---------- такой навык уже есть ----------

    [Theory]
    [InlineData("python", 1)]                  // название, другой регистр
    [InlineData("  PYTHON ", 1)]
    [InlineData("питон", 1)]                   // синоним
    [InlineData("Js", 2)]                      // синоним JavaScript
    public async Task A_skill_that_is_already_in_the_category_is_a_409_with_its_id(string name, int seedNumber)
    {
        var person = await NewPersonAsync();

        var response = await SuggestAsync(person, name, SkillCatalogSeed.It);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("skill_already_exists", body.GetProperty("code").GetString());
        Assert.Equal(SkillCatalogSeed.SkillId(seedNumber), body.GetProperty("existingSkillId").GetGuid());   // фронтенд предложит добавить его
    }

    [Fact]
    public async Task The_catalog_is_checked_by_the_name_in_every_language()
    {
        var person = await NewPersonAsync();

        foreach (var name in new[] { "Английский язык", "Забони англисӣ", "English" })
        {
            var response = await SuggestAsync(person, name, SkillCatalogSeed.Languages);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("skill_already_exists", body.GetProperty("code").GetString());
            Assert.Equal(SkillCatalogSeed.SkillId(12), body.GetProperty("existingSkillId").GetGuid());
        }
    }

    [Fact]
    public async Task The_same_name_is_fine_in_another_category_and_for_a_switched_off_skill()
    {
        var person = await NewPersonAsync();
        var marker = Unique("Gone");
        await _factory.QueryAsync(async db =>
        {
            var off = Skill.Create(SkillCatalogSeed.It, marker, marker, marker);
            off.Update(SkillCatalogSeed.It, marker, marker, marker, null, isActive: false);
            db.Skills.Add(off);
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(HttpStatusCode.Created, (await SuggestAsync(person, "Python", SkillCatalogSeed.Design)).StatusCode);   // в «Дизайне» такого нет
        Assert.Equal(HttpStatusCode.Created, (await SuggestAsync(person, marker, SkillCatalogSeed.It)).StatusCode);        // навык выключен
    }

    // ---------- повторы и лимит ----------

    [Fact]
    public async Task The_same_pending_suggestion_cannot_be_sent_twice_but_other_people_and_other_categories_can()
    {
        var first = await NewPersonAsync();
        var second = await NewPersonAsync();
        var name = Unique("Elixir");
        Assert.Equal(HttpStatusCode.Created, (await SuggestAsync(first, name)).StatusCode);

        var again = await SuggestAsync(first, name.ToUpperInvariant());   // другой регистр — тот же навык
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("suggestion_already_pending", await CodeAsync(again));

        Assert.Equal(HttpStatusCode.Created, (await SuggestAsync(second, name)).StatusCode);                              // другой человек: ещё один голос
        Assert.Equal(HttpStatusCode.Created, (await SuggestAsync(first, name, SkillCatalogSeed.Design)).StatusCode);      // другая категория
    }

    [Fact]
    public async Task At_most_ten_pending_suggestions_and_a_resolved_one_frees_a_slot_and_the_name()
    {
        var person = await NewPersonAsync();
        var names = Enumerable.Range(0, 11).Select(_ => Unique("Many")).ToList();
        foreach (var name in names.Take(10))
        {
            Assert.Equal(HttpStatusCode.Created, (await SuggestAsync(person, name)).StatusCode);
        }

        var eleventh = await SuggestAsync(person, names[10]);
        Assert.Equal(HttpStatusCode.Conflict, eleventh.StatusCode);
        Assert.Equal("too_many_pending_suggestions", await CodeAsync(eleventh));

        // Админ отклонил одно предложение (этап 7): место освободилось, и то же название можно предложить снова.
        await _factory.QueryAsync(async db =>
        {
            var row = await db.SkillSuggestions.SingleAsync(s => s.UserId == person.Id && s.NormalizedName == names[0].ToLowerInvariant());
            db.Entry(row).Property(s => s.Status).CurrentValue = SuggestionStatus.Rejected;
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.Equal(HttpStatusCode.Created, (await SuggestAsync(person, names[10])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SuggestAsync(person, names[0])).StatusCode);   // снова 10 неразобранных
    }

    [Fact]
    public async Task Parallel_requests_cannot_get_past_the_pending_limit()
    {
        var person = await NewPersonAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 14).Select(_ => SuggestAsync(person, Unique("Race"))));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(4, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));   // а не 500 и не 11 созданных
        Assert.Equal(10, await _factory.QueryAsync(db => db.SkillSuggestions.CountAsync(s => s.UserId == person.Id)));
    }

    [Fact]
    public async Task Parallel_identical_requests_create_exactly_one_suggestion()
    {
        var person = await NewPersonAsync();
        var name = Unique("Same");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SuggestAsync(person, name)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    // ---------- кто может ----------

    [Fact]
    public async Task Staff_accounts_cannot_suggest_skills()
    {
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Admin);
        await _factory.QueryAsync(async db =>
        {
            db.Users.Add(staff);
            await db.SaveChangesAsync();
            return 0;
        });
        var token = TestTokens.ForUser(_factory, staff);

        var response = await SendAsync(token, HttpMethod.Post, "/skills/suggestions", new { categoryId = SkillCatalogSeed.It, name = Unique() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("regular_account_required", await CodeAsync(response));
    }

    [Fact]
    public async Task A_blocked_account_cannot_suggest_skills()
    {
        var person = await NewPersonAsync();
        await _factory.QueryAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == person.Id);
            typeof(User).GetProperty(nameof(User.Status))!.SetValue(user, UserStatus.Blocked);
            await db.SaveChangesAsync();
            return 0;
        });

        var response = await SuggestAsync(person, Unique());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("account_not_active", await CodeAsync(response));
    }
}
