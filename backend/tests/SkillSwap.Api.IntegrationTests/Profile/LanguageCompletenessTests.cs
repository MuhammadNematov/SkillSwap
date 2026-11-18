using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Infrastructure.Persistence.Seed;

namespace SkillSwap.Api.IntegrationTests.Profile;

/// <summary>
/// Анкета полная (<c>skillsComplete</c>) только если указан хотя бы один язык занятий: иначе подбор пуст из-за обязательного
/// общего языка. Тот же признак управляет показом в подборе: сервер включает его, когда анкета стала полной, и скрывает, когда перестала.
/// </summary>
[Collection(ApiCollection.Name)]
public class LanguageCompletenessTests
{
    private readonly AuthApi _api;

    public LanguageCompletenessTests(ApiFactory factory) => _api = new AuthApi(factory);

    private sealed record Person(string Token, Guid Id);

    private async Task<Person> NewPersonAsync()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var token = (await _api.LoginSessionAsync(email)).AccessToken;
        var me = await JsonAsync(await SendAsync(token, HttpMethod.Get, "/me"));
        return new Person(token, me.GetProperty("id").GetGuid());
    }

    private async Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _api.Client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static object Entry(int skill) => new { skillId = SkillCatalogSeed.SkillId(skill), level = "Beginner", description = (string?)null };

    private async Task SaveSkillsAsync(Person p) =>
        (await SendAsync(p.Token, HttpMethod.Put, "/me/skills", new { teach = new[] { Entry(1) }, learn = new[] { Entry(24) } })).EnsureSuccessStatusCode();

    private Task<HttpResponseMessage> PutProfileAsync(Person p, string[] languages, bool visible = false, string name = "Тест") =>
        SendAsync(p.Token, HttpMethod.Put, "/me", new
        {
            name, bio = (string?)null, uiLanguage = "en", timeZone = "UTC", languages, isSearchable = visible,
        });

    private async Task<(bool Complete, bool Visible)> StateAsync(Person p)
    {
        var me = await JsonAsync(await SendAsync(p.Token, HttpMethod.Get, "/me"));
        return (me.GetProperty("skillsComplete").GetBoolean(), me.GetProperty("isSearchable").GetBoolean());
    }

    private async Task<bool> OthersCanSeeAsync(Person p)
    {
        var viewer = await NewPersonAsync();
        return (await SendAsync(viewer.Token, HttpMethod.Get, $"/users/{p.Id}")).StatusCode == HttpStatusCode.OK;
    }

    // ---------- без языка анкета не полная ----------

    [Fact]
    public async Task Skills_without_a_language_are_not_a_complete_anketa_anywhere_in_the_api()
    {
        var person = await NewPersonAsync();
        await SaveSkillsAsync(person);

        Assert.Equal((false, false), await StateAsync(person));

        var anketa = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me/skills"));
        Assert.False(anketa.GetProperty("isComplete").GetBoolean());
        Assert.False(anketa.GetProperty("isSearchable").GetBoolean());

        var matches = await SendAsync(person.Token, HttpMethod.Get, "/matches");
        Assert.Equal(HttpStatusCode.Forbidden, matches.StatusCode);
        Assert.Equal("skills_incomplete", await AuthApi.ReadCodeAsync(matches));
    }

    [Fact]
    public async Task The_save_response_of_the_skills_also_reports_the_missing_language()
    {
        var person = await NewPersonAsync();

        var put = await SendAsync(person.Token, HttpMethod.Put, "/me/skills", new { teach = new[] { Entry(1) }, learn = new[] { Entry(24) } });

        Assert.False((await JsonAsync(put)).GetProperty("isComplete").GetBoolean());
    }

    [Fact]
    public async Task Asking_to_be_visible_without_a_language_is_refused_and_nothing_is_saved()
    {
        var person = await NewPersonAsync();
        await SaveSkillsAsync(person);

        var refused = await PutProfileAsync(person, languages: [], visible: true, name: "Не должно сохраниться");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("skills_incomplete", await AuthApi.ReadCodeAsync(refused));
        var me = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me"));
        Assert.Equal("Test User", me.GetProperty("name").GetString());
        Assert.False(me.GetProperty("isSearchable").GetBoolean());
    }

    // ---------- язык делает анкету полной ----------

    [Fact]
    public async Task Adding_the_first_language_after_the_skills_completes_the_anketa_and_shows_the_user()
    {
        var person = await NewPersonAsync();
        await SaveSkillsAsync(person);

        var put = await PutProfileAsync(person, languages: ["ru"]);   // форма профиля шлёт «не виден»: это не решение скрыться

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var me = await JsonAsync(put);
        Assert.True(me.GetProperty("skillsComplete").GetBoolean());
        Assert.True(me.GetProperty("isSearchable").GetBoolean());   // первое заполнение анкеты включает показ
        Assert.True(await OthersCanSeeAsync(person));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(person.Token, HttpMethod.Get, "/matches")).StatusCode);   // и подбор открылся
    }

    [Fact]
    public async Task A_language_first_and_the_skills_second_complete_the_anketa_too()
    {
        var person = await NewPersonAsync();
        await PutProfileAsync(person, languages: ["en"]);
        Assert.Equal((false, false), await StateAsync(person));   // навыков ещё нет

        await SaveSkillsAsync(person);

        Assert.Equal((true, true), await StateAsync(person));
    }

    [Fact]
    public async Task Removing_the_only_language_makes_the_anketa_incomplete_and_hides_the_user_without_an_error()
    {
        var person = await NewPersonAsync();
        await SaveSkillsAsync(person);
        await PutProfileAsync(person, languages: ["en"]);
        Assert.Equal((true, true), await StateAsync(person));

        var cleared = await PutProfileAsync(person, languages: [], visible: true);   // форма шлёт «виден», языков уже нет

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var me = await JsonAsync(cleared);
        Assert.False(me.GetProperty("skillsComplete").GetBoolean());
        Assert.False(me.GetProperty("isSearchable").GetBoolean());   // скрыт сервером
        Assert.False(await OthersCanSeeAsync(person));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(person.Token, HttpMethod.Get, "/matches")).StatusCode);
    }

    [Fact]
    public async Task A_user_hidden_by_the_server_for_lack_of_a_language_is_shown_again_when_a_language_is_back()
    {
        var person = await NewPersonAsync();
        await SaveSkillsAsync(person);
        await PutProfileAsync(person, languages: ["en"]);
        await PutProfileAsync(person, languages: [], visible: true);   // сервер скрыл

        await PutProfileAsync(person, languages: ["tg"]);

        Assert.Equal((true, true), await StateAsync(person));   // скрыл не он сам, поэтому показывается снова
    }

    [Fact]
    public async Task A_user_who_hid_themselves_stays_hidden_when_the_anketa_becomes_complete_again()
    {
        var person = await NewPersonAsync();
        await SaveSkillsAsync(person);
        await PutProfileAsync(person, languages: ["en"]);
        await PutProfileAsync(person, languages: ["en"], visible: false);   // был виден и скрылся сам
        Assert.Equal((true, false), await StateAsync(person));

        await PutProfileAsync(person, languages: []);
        await PutProfileAsync(person, languages: ["ru"]);

        Assert.Equal((true, false), await StateAsync(person));   // полная, но по своей воле скрыт
        Assert.Equal(HttpStatusCode.OK, (await PutProfileAsync(person, languages: ["ru"], visible: true)).StatusCode);   // показаться он может сам
        Assert.Equal((true, true), await StateAsync(person));
    }

    [Fact]
    public async Task Changing_one_language_for_another_keeps_the_anketa_complete_and_the_user_visible()
    {
        var person = await NewPersonAsync();
        await SaveSkillsAsync(person);
        await PutProfileAsync(person, languages: ["en"]);

        var put = await PutProfileAsync(person, languages: ["fr", "de"], visible: true);

        var me = await JsonAsync(put);
        Assert.True(me.GetProperty("skillsComplete").GetBoolean());
        Assert.True(me.GetProperty("isSearchable").GetBoolean());
    }

    [Fact]
    public async Task Clearing_the_languages_of_a_hidden_incomplete_user_is_just_a_normal_save()
    {
        var person = await NewPersonAsync();

        var put = await PutProfileAsync(person, languages: [], name: "Без языков");

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Без языков", (await JsonAsync(put)).GetProperty("name").GetString());
    }

    // ---------- гонки ----------

    [Fact]
    public async Task Racing_language_changes_never_leave_a_visible_user_with_an_incomplete_anketa()
    {
        for (var round = 0; round < 6; round++)
        {
            var person = await NewPersonAsync();
            await SaveSkillsAsync(person);
            await PutProfileAsync(person, languages: ["en"]);

            await Task.WhenAll(
                PutProfileAsync(person, languages: [], visible: true),
                PutProfileAsync(person, languages: ["ru"], visible: true),
                PutProfileAsync(person, languages: [], visible: true),
                SendAsync(person.Token, HttpMethod.Put, "/me/skills", new { teach = new[] { Entry(1) }, learn = new[] { Entry(24) } }));

            var (complete, visible) = await StateAsync(person);
            Assert.False(visible && !complete, $"round {round}: виден с неполной анкетой");
        }
    }
}
