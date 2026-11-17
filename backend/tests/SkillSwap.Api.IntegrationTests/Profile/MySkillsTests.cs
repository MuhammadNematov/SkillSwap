using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence.Seed;

namespace SkillSwap.Api.IntegrationTests.Profile;

/// <summary>Анкета (раздел 5.2 ТЗ): GET/PUT /me/skills, «Умею» и «Хочу изучать», полнота анкеты и видимость в подборе.</summary>
[Collection(ApiCollection.Name)]
public class MySkillsTests
{
    // Стартовые навыки (SkillCatalogSeed): 1 = Python, 2 = JavaScript, 3 = C#, 24 = Figma.
    private static Guid Python => SkillCatalogSeed.SkillId(1);
    private static Guid JavaScript => SkillCatalogSeed.SkillId(2);
    private static Guid CSharp => SkillCatalogSeed.SkillId(3);
    private static Guid Figma => SkillCatalogSeed.SkillId(24);

    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public MySkillsTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    private sealed record Person(string Token, Guid Id);

    private async Task<Person> NewPersonAsync()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var token = (await _api.LoginSessionAsync(email)).AccessToken;
        var me = await JsonAsync(await SendAsync(token, HttpMethod.Get, "/me"));
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

    private async Task<HttpResponseMessage> SendRawAsync(string token, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/skills") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _api.Client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static object Entry(Guid skill, string level = "Intermediate", string? description = null) =>
        new { skillId = skill, level, description };

    private Task<HttpResponseMessage> SaveAsync(Person person, object[] teach, object[] learn) =>
        SendAsync(person.Token, HttpMethod.Put, "/me/skills", new { teach, learn });

    private async Task<HttpResponseMessage> CompleteAnketaAsync(Person person)
    {
        var response = await SaveAsync(person, [Entry(Python, "Advanced")], [Entry(Figma, "Beginner")]);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static List<Guid> Ids(JsonElement anketa, string direction) =>
        anketa.GetProperty(direction).EnumerateArray().Select(s => s.GetProperty("skillId").GetGuid()).ToList();

    private Task<int> ModifyAsync(Func<SkillSwap.Infrastructure.Persistence.AppDbContext, Task> change) => _factory.QueryAsync(async db =>
    {
        await change(db);
        await db.SaveChangesAsync();
        return 0;
    });

    // ---------- доступ ----------

    [Fact]
    public async Task Both_endpoints_require_a_login()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/me/skills")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Put, "/me/skills", new { teach = Array.Empty<object>(), learn = Array.Empty<object>() })).StatusCode);
    }

    // ---------- чтение и запись ----------

    [Fact]
    public async Task A_new_user_has_an_empty_incomplete_anketa()
    {
        var person = await NewPersonAsync();

        var anketa = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me/skills"));

        Assert.Equal(0, anketa.GetProperty("teach").GetArrayLength());
        Assert.Equal(0, anketa.GetProperty("learn").GetArrayLength());
        Assert.False(anketa.GetProperty("isComplete").GetBoolean());
        Assert.False(anketa.GetProperty("isSearchable").GetBoolean());
    }

    [Fact]
    public async Task The_anketa_is_saved_and_read_back_with_catalog_names_levels_and_descriptions()
    {
        var person = await NewPersonAsync();

        var put = await SaveAsync(person,
            [Entry(Python, "Advanced", "  10 лет в разработке  ")],
            [Entry(JavaScript, "Beginner")]);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await JsonAsync(put);
        var teach = saved.GetProperty("teach")[0];
        Assert.Equal(Python, teach.GetProperty("skillId").GetGuid());
        Assert.Equal(SkillCatalogSeed.It, teach.GetProperty("categoryId").GetGuid());
        Assert.Equal("Python", teach.GetProperty("nameEn").GetString());
        Assert.Equal("Advanced", teach.GetProperty("level").GetString());
        Assert.Equal("10 лет в разработке", teach.GetProperty("description").GetString());   // пробелы по краям убраны
        Assert.True(teach.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("learn")[0].GetProperty("description").ValueKind);
        Assert.True(saved.GetProperty("isComplete").GetBoolean());

        var read = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me/skills"));
        Assert.Equal(saved.GetRawText(), read.GetRawText());
        Assert.True((await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me"))).GetProperty("anketaComplete").GetBoolean());
    }

    [Fact]
    public async Task Saving_replaces_the_list_keeps_the_rows_that_stay_and_removes_the_rest()
    {
        var person = await NewPersonAsync();
        await SaveAsync(person, [Entry(Python, "Beginner"), Entry(CSharp)], [Entry(Figma)]);
        var before = await _factory.QueryAsync(db => db.UserSkills.Where(s => s.UserId == person.Id).ToDictionaryAsync(s => (s.SkillId, s.Direction), s => s.Id));

        var put = await SaveAsync(person, [Entry(Python, "Advanced", "теперь опытнее"), Entry(JavaScript)], [Entry(Figma)]);

        var anketa = await JsonAsync(put);
        Assert.Equal(new[] { JavaScript, Python }.Order(), Ids(anketa, "teach").Order());   // C# убран, JavaScript добавлен
        var python = anketa.GetProperty("teach").EnumerateArray().Single(s => s.GetProperty("skillId").GetGuid() == Python);
        Assert.Equal("Advanced", python.GetProperty("level").GetString());
        Assert.Equal("теперь опытнее", python.GetProperty("description").GetString());

        var after = await _factory.QueryAsync(db => db.UserSkills.Where(s => s.UserId == person.Id).ToDictionaryAsync(s => (s.SkillId, s.Direction), s => s.Id));
        Assert.Equal(before[(Python, SkillDirection.Teach)], after[(Python, SkillDirection.Teach)]);   // та же строка, не пересоздана
        Assert.False(after.ContainsKey((CSharp, SkillDirection.Teach)));
        Assert.Equal(3, after.Count);
    }

    [Fact]
    public async Task The_same_skill_may_be_in_both_directions_but_only_once_in_each()
    {
        var person = await NewPersonAsync();

        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(person, [Entry(Python)], [Entry(Python)])).StatusCode);

        var twice = await SaveAsync(person, [Entry(Python), Entry(Python, "Advanced")], [Entry(Figma)]);
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
        Assert.Equal("validation_failed", await AuthApi.ReadCodeAsync(twice));
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(person, [Entry(Python)], [Entry(Figma), Entry(Figma)])).StatusCode);
    }

    [Fact]
    public async Task An_empty_list_clears_a_direction_and_both_lists_are_required()
    {
        var person = await NewPersonAsync();
        await CompleteAnketaAsync(person);

        var cleared = await JsonAsync(await SaveAsync(person, [], []));
        Assert.Equal(0, cleared.GetProperty("teach").GetArrayLength());
        Assert.False(cleared.GetProperty("isComplete").GetBoolean());

        await CompleteAnketaAsync(person);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendRawAsync(person.Token, """{"learn":[]}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendRawAsync(person.Token, """{"teach":[]}""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendRawAsync(person.Token, """{"teach":null,"learn":null}""")).StatusCode);

        // Отказ не стирает анкету.
        Assert.Equal(1, (await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me/skills"))).GetProperty("teach").GetArrayLength());
    }

    // ---------- проверка данных ----------

    [Fact]
    public async Task Skills_that_are_unknown_switched_off_or_in_a_switched_off_category_are_refused_and_nothing_changes()
    {
        var person = await NewPersonAsync();
        await CompleteAnketaAsync(person);
        var marker = "Off" + Guid.NewGuid().ToString("N")[..8];
        Guid offSkill = default, skillInOffCategory = default;
        await ModifyAsync(db =>
        {
            var off = Skill.Create(SkillCatalogSeed.It, marker, marker, marker);
            off.Update(SkillCatalogSeed.It, marker, marker, marker, null, isActive: false);
            var category = SkillCategory.Create(marker, marker, marker, 90);
            category.Update(marker, marker, marker, 90, isActive: false);
            var inside = Skill.Create(category.Id, marker, marker, marker);
            offSkill = off.Id;
            skillInOffCategory = inside.Id;
            db.SkillCategories.Add(category);
            db.Skills.AddRange(off, inside);
            return Task.CompletedTask;
        });

        foreach (var bad in new[] { Guid.NewGuid(), offSkill, skillInOffCategory })
        {
            var response = await SaveAsync(person, [Entry(JavaScript), Entry(bad)], [Entry(Figma)]);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("unknown_skill", await AuthApi.ReadCodeAsync(response));
        }

        var anketa = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me/skills"));
        Assert.Equal([Python], Ids(anketa, "teach"));   // прежняя анкета на месте
    }

    [Fact]
    public async Task Entries_with_a_bad_level_description_or_id_are_refused()
    {
        var person = await NewPersonAsync();
        string Body(string entry) => $$"""{"teach":[{{entry}}],"learn":[]}""";

        var cases = new[]
        {
            $$"""{"skillId":"{{Python}}","level":"Expert"}""",           // нет такого уровня
            $$"""{"skillId":"{{Python}}","level":7}""",                  // число вне списка
            $$"""{"skillId":"{{Python}}","level":0}""",
            $$"""{"skillId":"{{Python}}"}""",                            // уровень не указан
            $$"""{"skillId":"{{Python}}","level":null}""",
            $$"""{"level":"Beginner"}""",                                 // нет навыка
            $$"""{"skillId":"{{Guid.Empty}}","level":"Beginner"}""",
            $$"""{"skillId":"not-a-guid","level":"Beginner"}""",
            $$"""{"skillId":"{{Python}}","level":"Beginner","description":"{{new string('x', 501)}}"}""",
            "null",
        };

        foreach (var entry in cases)
        {
            var response = await SendRawAsync(person.Token, Body(entry));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{entry[..Math.Min(entry.Length, 60)]} → {response.StatusCode}");
        }

        Assert.Equal(HttpStatusCode.OK, (await SendRawAsync(person.Token,
            Body($$"""{"skillId":"{{Python}}","level":"Beginner","description":"{{new string('я', 500)}}"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendRawAsync(person.Token, Body($$"""{"skillId":"{{Python}}","level":"beginner"}"""))).StatusCode);   // регистр уровня не важен
    }

    [Fact]
    public async Task At_most_thirty_skills_per_direction()
    {
        var person = await NewPersonAsync();
        var ids = await _factory.QueryAsync(async db =>
        {
            var category = SkillCategory.Create("Many" + Guid.NewGuid().ToString("N")[..8], "m", "m", 91);
            db.SkillCategories.Add(category);
            var skills = Enumerable.Range(0, 31).Select(i => Skill.Create(category.Id, $"s{i}", $"s{i}", $"s{i}")).ToList();
            db.Skills.AddRange(skills);
            await db.SaveChangesAsync();
            return skills.Select(s => s.Id).ToList();
        });

        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(person, ids.Take(30).Select(i => Entry(i)).ToArray(), [Entry(Figma)])).StatusCode);
        var tooMany = await SaveAsync(person, ids.Select(i => Entry(i)).ToArray(), [Entry(Figma)]);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    // ---------- полнота анкеты и видимость ----------

    [Fact]
    public async Task One_skill_in_each_direction_makes_the_anketa_complete_one_direction_does_not()
    {
        var person = await NewPersonAsync();

        Assert.False((await JsonAsync(await SaveAsync(person, [Entry(Python)], []))).GetProperty("isComplete").GetBoolean());
        Assert.False((await JsonAsync(await SaveAsync(person, [], [Entry(Python)]))).GetProperty("isComplete").GetBoolean());
        Assert.True((await JsonAsync(await SaveAsync(person, [Entry(Python)], [Entry(JavaScript)]))).GetProperty("isComplete").GetBoolean());
    }

    private static object Profile(bool isSearchable, string name = "Тест") =>
        new { name, bio = (string?)null, uiLanguage = "en", timeZone = "UTC", languages = new[] { "en" }, isSearchable };

    [Fact]
    public async Task Without_a_complete_anketa_a_user_cannot_make_themselves_visible()
    {
        var person = await NewPersonAsync();
        await SaveAsync(person, [Entry(Python)], []);   // только «Умею»

        var refused = await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(true, name: "Не должно сохраниться"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("anketa_incomplete", await AuthApi.ReadCodeAsync(refused));
        var me = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me"));
        Assert.Equal("Test User", me.GetProperty("name").GetString());   // ничего из запроса не сохранилось
        Assert.False(me.GetProperty("isSearchable").GetBoolean());

        await SaveAsync(person, [Entry(Python)], [Entry(Figma)]);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(false))).StatusCode);   // скрыться можно всегда
    }

    [Fact]
    public async Task An_anketa_that_becomes_incomplete_hides_the_user_from_matching()
    {
        var person = await NewPersonAsync();
        await CompleteAnketaAsync(person);
        await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(true));
        Assert.True((await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me"))).GetProperty("isSearchable").GetBoolean());

        var put = await SaveAsync(person, [Entry(Python)], []);

        var anketa = await JsonAsync(put);
        Assert.False(anketa.GetProperty("isComplete").GetBoolean());
        Assert.False(anketa.GetProperty("isSearchable").GetBoolean());   // скрыт сервером
        Assert.False((await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me"))).GetProperty("isSearchable").GetBoolean());
    }

    [Fact]
    public async Task A_complete_anketa_does_not_make_a_user_visible_by_itself()
    {
        var person = await NewPersonAsync();

        var anketa = await JsonAsync(await CompleteAnketaAsync(person));

        Assert.True(anketa.GetProperty("isComplete").GetBoolean());
        Assert.False(anketa.GetProperty("isSearchable").GetBoolean());   // видимость включается отдельно, PUT /me
    }

    [Fact]
    public async Task Racing_visibility_against_clearing_the_anketa_never_leaves_a_visible_incomplete_profile()
    {
        for (var round = 0; round < 6; round++)
        {
            var person = await NewPersonAsync();
            await CompleteAnketaAsync(person);

            await Task.WhenAll(
                SendAsync(person.Token, HttpMethod.Put, "/me", Profile(true)),
                SaveAsync(person, [Entry(Python)], []),
                SendAsync(person.Token, HttpMethod.Put, "/me", Profile(true)));

            var me = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me"));
            Assert.False(me.GetProperty("isSearchable").GetBoolean() && !me.GetProperty("anketaComplete").GetBoolean(),
                $"round {round}: виден с неполной анкетой");
        }
    }

    // ---------- навыки, выключенные админом ----------

    [Fact]
    public async Task A_skill_switched_off_by_the_admin_is_flagged_in_my_anketa_and_hidden_from_others()
    {
        var owner = await NewPersonAsync();
        var other = await NewPersonAsync();
        var marker = "Gone" + Guid.NewGuid().ToString("N")[..8];
        var skillId = await _factory.QueryAsync(async db =>
        {
            var skill = Skill.Create(SkillCatalogSeed.It, marker, marker, marker);
            db.Skills.Add(skill);
            await db.SaveChangesAsync();
            return skill.Id;
        });
        await SaveAsync(owner, [Entry(skillId)], [Entry(Figma)]);
        await SendAsync(owner.Token, HttpMethod.Put, "/me", Profile(true));

        await ModifyAsync(async db => (await db.Skills.SingleAsync(s => s.Id == skillId)).Update(SkillCatalogSeed.It, marker, marker, marker, null, false));

        var mine = await JsonAsync(await SendAsync(owner.Token, HttpMethod.Get, "/me/skills"));
        Assert.False(mine.GetProperty("teach")[0].GetProperty("isActive").GetBoolean());
        Assert.False(mine.GetProperty("isComplete").GetBoolean());   // единственный навык «Умею» выключен
        Assert.False((await JsonAsync(await SendAsync(owner.Token, HttpMethod.Get, "/me"))).GetProperty("anketaComplete").GetBoolean());

        var publicView = await JsonAsync(await SendAsync(other.Token, HttpMethod.Get, $"/users/{owner.Id}"));
        Assert.Equal(0, publicView.GetProperty("teach").GetArrayLength());   // другим он не виден

        // Выключенный навык нельзя оставить в новой версии анкеты, но убрать можно.
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(owner, [Entry(skillId)], [Entry(Figma)])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(owner, [Entry(Python)], [Entry(Figma)])).StatusCode);
    }

    [Fact]
    public async Task A_skill_that_is_in_someones_anketa_cannot_be_deleted_from_the_catalog_only_switched_off()
    {
        var person = await NewPersonAsync();
        var admin = TestTokens.For(_factory, UserRole.Admin);
        var marker = "Used" + Guid.NewGuid().ToString("N")[..8];
        var created = await JsonAsync(await SendAsync(admin, HttpMethod.Post, "/admin/skills",
            new { categoryId = SkillCatalogSeed.It, nameTg = marker, nameRu = marker, nameEn = marker }));
        var skillId = created.GetProperty("id").GetGuid();
        await SaveAsync(person, [Entry(skillId)], [Entry(Figma)]);

        var blocked = await SendAsync(admin, HttpMethod.Delete, $"/admin/skills/{skillId}");

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("skill_in_use", await AuthApi.ReadCodeAsync(blocked));
        Assert.Equal(1, (await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me/skills"))).GetProperty("teach").GetArrayLength());   // анкета цела

        await SaveAsync(person, [Entry(Python)], [Entry(Figma)]);   // пользователь убрал навык
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(admin, HttpMethod.Delete, $"/admin/skills/{skillId}")).StatusCode);
    }

    // ---------- чужой профиль ----------

    [Fact]
    public async Task The_public_profile_shows_the_skills_with_levels_and_descriptions()
    {
        var owner = await NewPersonAsync();
        var other = await NewPersonAsync();
        await SaveAsync(owner, [Entry(Python, "Advanced", "10 лет"), Entry(CSharp, "Intermediate")], [Entry(Figma, "Beginner", "хочу в дизайн")]);
        await SendAsync(owner.Token, HttpMethod.Put, "/me", Profile(true));

        var profile = await JsonAsync(await SendAsync(other.Token, HttpMethod.Get, $"/users/{owner.Id}"));

        Assert.Equal(new[] { CSharp, Python }.Order(), Ids(profile, "teach").Order());
        var python = profile.GetProperty("teach").EnumerateArray().Single(s => s.GetProperty("skillId").GetGuid() == Python);
        Assert.Equal("Advanced", python.GetProperty("level").GetString());
        Assert.Equal("10 лет", python.GetProperty("description").GetString());
        Assert.Equal("Figma", profile.GetProperty("learn")[0].GetProperty("nameEn").GetString());
        Assert.False(profile.GetProperty("teach")[0].TryGetProperty("isActive", out _));
    }

    // ---------- служебные и заблокированные ----------

    [Fact]
    public async Task Staff_have_no_anketa()
    {
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Moderator);
        await ModifyAsync(db => { db.Users.Add(staff); return Task.CompletedTask; });
        var token = TestTokens.ForUser(_factory, staff);

        var get = await SendAsync(token, HttpMethod.Get, "/me/skills");
        var put = await SendAsync(token, HttpMethod.Put, "/me/skills", new { teach = new[] { Entry(Python) }, learn = new[] { Entry(Figma) } });

        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal("staff_has_no_profile", await AuthApi.ReadCodeAsync(put));
    }

    [Fact]
    public async Task A_blocked_account_cannot_change_its_anketa()
    {
        var person = await NewPersonAsync();
        await ModifyAsync(async db => typeof(User).GetProperty(nameof(User.Status))!
            .SetValue(await db.Users.SingleAsync(u => u.Id == person.Id), UserStatus.Blocked));

        var put = await SaveAsync(person, [Entry(Python)], [Entry(Figma)]);

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal("account_not_active", await AuthApi.ReadCodeAsync(put));
    }

    // ---------- гонки ----------

    [Fact]
    public async Task Parallel_saves_never_fail_with_500_and_leave_one_whole_anketa()
    {
        var person = await NewPersonAsync();
        var sets = new[]
        {
            (Teach: new[] { Python }, Learn: new[] { Figma }),
            (Teach: new[] { JavaScript, CSharp }, Learn: new[] { Python }),
            (Teach: new[] { CSharp }, Learn: new[] { JavaScript, Figma }),
        };

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
        {
            var set = sets[i % sets.Length];
            return SaveAsync(person, set.Teach.Select(s => Entry(s)).ToArray(), set.Learn.Select(s => Entry(s)).ToArray());
        }));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"got {r.StatusCode}"));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);

        var anketa = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me/skills"));
        var teach = Ids(anketa, "teach").Order().ToList();
        var learn = Ids(anketa, "learn").Order().ToList();
        Assert.Contains(sets, s => s.Teach.Order().SequenceEqual(teach) && s.Learn.Order().SequenceEqual(learn));   // и «Умею», и «Хочу» — из одного запроса
    }
}
