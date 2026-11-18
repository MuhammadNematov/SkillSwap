using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Infrastructure.Persistence.Seed;

namespace SkillSwap.Api.IntegrationTests.Profile;

/// <summary>
/// Видимость в подборе после заполнения анкеты: сервер включает её сам, пока пользователь не скрылся вручную;
/// скрывшегося сам больше не включает.
/// </summary>
[Collection(ApiCollection.Name)]
public class AutoVisibilityTests
{
    private readonly AuthApi _api;

    public AutoVisibilityTests(ApiFactory factory) => _api = new AuthApi(factory);

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

    private async Task<bool> SaveSkillsAsync(Person p, int[] teach, int[] learn)
    {
        var put = await SendAsync(p.Token, HttpMethod.Put, "/me/skills", new { teach = teach.Select(Entry), learn = learn.Select(Entry) });
        put.EnsureSuccessStatusCode();
        return (await JsonAsync(put)).GetProperty("isSearchable").GetBoolean();
    }

    private Task<bool> CompleteAsync(Person p) => SaveSkillsAsync(p, [1], [24]);

    private async Task SetVisibilityAsync(Person p, bool visible) =>
        (await SendAsync(p.Token, HttpMethod.Put, "/me", new
        {
            name = "Тест", bio = (string?)null, uiLanguage = "en", timeZone = "UTC", languages = new[] { "en" }, isSearchable = visible,
        })).EnsureSuccessStatusCode();

    private async Task<bool> IsVisibleAsync(Person p) =>
        (await JsonAsync(await SendAsync(p.Token, HttpMethod.Get, "/me"))).GetProperty("isSearchable").GetBoolean();

    private async Task<bool> OthersCanSeeAsync(Person p)
    {
        var viewer = await NewPersonAsync();
        return (await SendAsync(viewer.Token, HttpMethod.Get, $"/users/{p.Id}")).StatusCode == HttpStatusCode.OK;
    }

    [Fact]
    public async Task Before_any_skills_a_new_user_is_hidden_and_the_first_complete_anketa_shows_them()
    {
        var person = await NewPersonAsync();
        Assert.False(await IsVisibleAsync(person));

        Assert.False(await SaveSkillsAsync(person, [1], []));    // половина анкеты — ещё скрыт
        Assert.True(await SaveSkillsAsync(person, [1], [24]));   // полная — показан сам

        Assert.True(await IsVisibleAsync(person));
        Assert.True(await OthersCanSeeAsync(person));
    }

    [Fact]
    public async Task A_default_hidden_profile_saved_before_the_anketa_does_not_count_as_hiding_on_purpose()
    {
        var person = await NewPersonAsync();
        await SetVisibilityAsync(person, false);   // форма профиля шлёт текущее значение «скрыт»

        Assert.True(await CompleteAsync(person));
    }

    [Fact]
    public async Task A_user_who_hid_themselves_is_never_shown_automatically_again()
    {
        var person = await NewPersonAsync();
        Assert.True(await CompleteAsync(person));
        await SetVisibilityAsync(person, false);   // скрылся сам

        Assert.False(await SaveSkillsAsync(person, [1, 2], [24]));        // анкета правится и остаётся полной
        Assert.False(await SaveSkillsAsync(person, [1], []));             // стала неполной
        Assert.False(await SaveSkillsAsync(person, [1], [24]));           // снова полная: всё равно скрыт

        Assert.False(await IsVisibleAsync(person));
        Assert.False(await OthersCanSeeAsync(person));
    }

    [Fact]
    public async Task Showing_oneself_again_cancels_the_earlier_decision()
    {
        var person = await NewPersonAsync();
        await CompleteAsync(person);
        await SetVisibilityAsync(person, false);
        await SetVisibilityAsync(person, true);    // снова показался сам

        Assert.False(await SaveSkillsAsync(person, [1], []));    // сервер скрыл из-за неполной анкеты
        Assert.True(await SaveSkillsAsync(person, [1], [24]));   // заполнил — показан автоматически
    }

    [Fact]
    public async Task A_user_hidden_by_the_server_because_of_an_incomplete_anketa_is_shown_again_when_it_is_complete()
    {
        var person = await NewPersonAsync();
        Assert.True(await CompleteAsync(person));

        Assert.False(await SaveSkillsAsync(person, [1], []));
        Assert.False(await IsVisibleAsync(person));

        Assert.True(await SaveSkillsAsync(person, [2], [25]));   // скрыл не он сам, а сервер
    }

    [Fact]
    public async Task Editing_a_complete_anketa_keeps_a_visible_user_visible()
    {
        var person = await NewPersonAsync();
        Assert.True(await CompleteAsync(person));

        Assert.True(await SaveSkillsAsync(person, [1, 2, 3], [24, 25]));
    }

    [Fact]
    public async Task Staying_hidden_by_choice_survives_profile_edits_that_keep_it_hidden()
    {
        var person = await NewPersonAsync();
        await CompleteAsync(person);
        await SetVisibilityAsync(person, false);
        await SetVisibilityAsync(person, false);   // правка профиля с тем же «скрыт» не отменяет решения

        Assert.False(await SaveSkillsAsync(person, [1], [24]));
    }
}
