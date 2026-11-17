using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkillSwap.Api.IntegrationTests.Infrastructure;

namespace SkillSwap.Api.IntegrationTests.Profile;

/// <summary>GET /languages: справочник языков занятий для фронтенда.</summary>
[Collection(ApiCollection.Name)]
public class LanguagesEndpointTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public LanguagesEndpointTests(ApiFactory factory)
    {
        _factory = factory;
        _client = new AuthApi(factory).Client;
    }

    [Fact]
    public async Task The_list_is_public_cacheable_and_has_codes_with_names_in_three_languages()
    {
        var response = await _client.GetAsync("/api/v1/languages");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("max-age=86400", response.Headers.CacheControl!.ToString());
        Assert.True(response.Headers.CacheControl.Public);

        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.True(items.Count > 170);

        var russian = items.Single(i => i.GetProperty("code").GetString() == "ru");
        Assert.Equal("Русӣ", russian.GetProperty("nameTg").GetString());
        Assert.Equal("Русский", russian.GetProperty("nameRu").GetString());
        Assert.Equal("Russian", russian.GetProperty("nameEn").GetString());

        Assert.All(items, i =>
        {
            Assert.Matches("^[a-z]{2}$", i.GetProperty("code").GetString()!);
            foreach (var name in new[] { "nameTg", "nameRu", "nameEn" })
            {
                Assert.False(string.IsNullOrWhiteSpace(i.GetProperty(name).GetString()));
            }
        });
        Assert.Equal(items.Count, items.Select(i => i.GetProperty("code").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task Every_code_the_list_offers_is_accepted_by_the_profile()
    {
        // Справочник для фронтенда и проверка профиля обязаны совпадать: иначе пользователь выберет язык, который сервер отвергнет.
        var api = new AuthApi(_factory);
        var email = await api.RegisterConfirmedUserAsync();
        var token = (await api.LoginSessionAsync(email)).AccessToken;
        var codes = (await (await _client.GetAsync("/api/v1/languages")).Content.ReadFromJsonAsync<JsonElement>())
            .EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToList();

        foreach (var chunk in codes.Chunk(10))
        {
            var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/me")
            {
                Content = JsonContent.Create(new { name = "Тест", uiLanguage = "en", timeZone = "UTC", languages = chunk, isSearchable = false }),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var response = await _client.SendAsync(request);

            Assert.True(response.StatusCode == HttpStatusCode.OK, $"codes {string.Join(",", chunk)}: {response.StatusCode}");
        }
    }
}
