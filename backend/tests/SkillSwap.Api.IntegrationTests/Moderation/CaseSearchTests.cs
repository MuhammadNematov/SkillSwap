using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>
/// Серверный поиск <c>q</c> в списках обращений (админ и модератор) и запросов на блокировку: часть имени или почты участников либо текста, без учёта регистра.
/// Уникальная метка в каждом тесте отделяет свои записи от записей других тестов в общей базе.
/// </summary>
[Collection(ApiCollection.Name)]
public class CaseSearchTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public CaseSearchTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<HttpResponseMessage> GetAsync(string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1" + url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<List<Guid>> IdsAsync(string token, string url)
    {
        var response = await GetAsync(token, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>Обмен двух людей с заданными именами и почтами и жалоба заявителя с заданным текстом; возвращает id обращения.</summary>
    private async Task<(Guid CaseId, MatchPerson Opener, MatchPerson Against)> CaseAsync(string openerName, string openerEmail, string againstName, string againstEmail, string description)
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Name, openerName).SetProperty(u => u.Email, openerEmail).SetProperty(u => u.NormalizedEmail, openerEmail.ToUpperInvariant()));
            await db.Users.Where(u => u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Name, againstName).SetProperty(u => u.Email, againstEmail).SetProperty(u => u.NormalizedEmail, againstEmail.ToUpperInvariant()));
            return 0;
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases") { Content = JsonContent.Create(new { type = "Conduct", category = "Insults", exchangeId, description }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", a.Token);
        var created = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return ((await JsonAsync(created)).GetProperty("id").GetGuid(), a, b);
    }

    private const string Text = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости";

    [Fact]
    public async Task Admin_finds_cases_by_a_part_of_either_participants_name_email_or_text_ignoring_case()
    {
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var t = Tag();
        var (mine, _, _) = await CaseAsync($"Иванова-{t}", $"opener-{t}@example.com", $"Петров-{t}", $"against-{t}@example.com", $"{Text}. Метка текста: уник{t}ум");
        var (other, _, _) = await CaseAsync($"Сидоров-{t}x", $"third-{t}@example.com", $"Козлов-{t}x", $"fourth-{t}@example.com", Text + ". Другое");

        Assert.Equal([mine], await IdsAsync(admin.Token, $"/admin/cases?q=ИВАНОВА-{t}"));        // имя заявителя, другой регистр
        Assert.Equal([mine], await IdsAsync(admin.Token, $"/admin/cases?q=петров-{t.ToUpperInvariant()}"));   // имя того, на кого жалоба
        Assert.Equal([mine], await IdsAsync(admin.Token, $"/admin/cases?q=OPENER-{t}@EXAMPLE"));     // почта заявителя
        Assert.Equal([mine], await IdsAsync(admin.Token, $"/admin/cases?q=against-{t}"));            // почта второго участника
        Assert.Equal([mine], await IdsAsync(admin.Token, $"/admin/cases?q=УНИК{t}УМ"));              // текст обращения
        Assert.Equal([other], await IdsAsync(admin.Token, $"/admin/cases?q=сидоров-{t}"));
        Assert.Empty(await IdsAsync(admin.Token, $"/admin/cases?q=нет-такого-{t}"));
    }

    [Fact]
    public async Task Search_combines_with_filters_ignores_blank_and_rejects_a_too_long_query()
    {
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var t = Tag();
        var (id, _, _) = await CaseAsync($"Анна-{t}", $"anna-{t}@example.com", $"Борис-{t}", $"boris-{t}@example.com", Text);

        Assert.Equal([id], await IdsAsync(admin.Token, $"/admin/cases?q=анна-{t}&status=Open&type=Conduct"));
        Assert.Empty(await IdsAsync(admin.Token, $"/admin/cases?q=анна-{t}&status=Resolved"));
        Assert.Empty(await IdsAsync(admin.Token, $"/admin/cases?q=анна-{t}&type=ReviewDispute"));

        var all = (await JsonAsync(await GetAsync(admin.Token, "/admin/cases?pageSize=1"))).GetProperty("totalCount").GetInt32();
        var blank = (await JsonAsync(await GetAsync(admin.Token, "/admin/cases?pageSize=1&q=%20%20"))).GetProperty("totalCount").GetInt32();
        Assert.Equal(all, blank);   // пустой поиск = без поиска

        var tooLong = await GetAsync(admin.Token, $"/admin/cases?q={new string('я', 101)}");
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("invalid_search", (await JsonAsync(tooLong)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(admin.Token, $"/admin/cases?q={new string('я', 100)}")).StatusCode);
    }

    [Fact]
    public async Task Percent_underscore_and_backslash_in_the_query_are_ordinary_characters()
    {
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var t = Tag();
        var (withSigns, _, _) = await CaseAsync($"Скидка100%_{t}\\", $"s-{t}@example.com", $"Обычный-{t}", $"o-{t}@example.com", Text);
        var (plain, _, _) = await CaseAsync($"Скидка100ab{t}", $"p-{t}@example.com", $"Другой-{t}", $"d-{t}@example.com", Text);

        Assert.Equal([withSigns], await IdsAsync(admin.Token, $"/admin/cases?q={Uri.EscapeDataString($"100%_{t}\\")}"));
        Assert.Equal([plain], await IdsAsync(admin.Token, $"/admin/cases?q={Uri.EscapeDataString($"100ab{t}")}"));
        Assert.Empty(await IdsAsync(admin.Token, $"/admin/cases?q={Uri.EscapeDataString($"100_{t}")}"));   // «_» не «любой символ»
        Assert.Empty(await IdsAsync(admin.Token, $"/admin/cases?q={Uri.EscapeDataString($"1%{t}")}"));    // «%» не «что угодно»
    }

    [Fact]
    public async Task Moderator_searches_the_queue_and_own_cases_by_the_same_rules()
    {
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var t = Tag();
        var (mine, _, _) = await CaseAsync($"Лена-{t}", $"lena-{t}@example.com", $"Макс-{t}", $"max-{t}@example.com", Text + $". Фраза-{t}");
        var (other, _, _) = await CaseAsync($"Олег-{t}", $"oleg-{t}@example.com", $"Паша-{t}", $"pasha-{t}@example.com", Text);

        Assert.Equal([mine], await IdsAsync(moderator.Token, $"/moderation/cases?q=ЛЕНА-{t}&pageSize=50"));
        Assert.Equal([mine], await IdsAsync(moderator.Token, $"/moderation/cases?q=max-{t}@&pageSize=50"));
        Assert.Equal([mine], await IdsAsync(moderator.Token, $"/moderation/cases?q=фраза-{t}&pageSize=50"));
        Assert.Equal([other], await IdsAsync(moderator.Token, $"/moderation/cases?q=олег-{t}&scope=Queue&pageSize=50"));
        Assert.Empty(await IdsAsync(moderator.Token, $"/moderation/cases?q=лена-{t}&scope=Mine&pageSize=50"));   // ещё не взято

        var take = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/moderation/cases/{mine}/take");
        take.Headers.Authorization = new AuthenticationHeaderValue("Bearer", moderator.Token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(take)).StatusCode);
        Assert.Equal([mine], await IdsAsync(moderator.Token, $"/moderation/cases?q=лена-{t}&scope=Mine&pageSize=50"));
        Assert.Empty(await IdsAsync(moderator.Token, $"/moderation/cases?q=лена-{t}&scope=Queue&pageSize=50"));   // из очереди ушло

        var tooLong = await GetAsync(moderator.Token, $"/moderation/cases?q={new string('я', 101)}");
        Assert.Equal("invalid_search", (await JsonAsync(tooLong)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_moderator_does_not_find_cases_with_a_conflict_of_interest_by_search()
    {
        var t = Tag();
        var (id, opener, _) = await CaseAsync($"Конфликт-{t}", $"conf-{t}@example.com", $"Второй-{t}", $"sec-{t}@example.com", Text);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator, linkedPersonal: opener);   // личный аккаунт модератора — участник

        Assert.Empty(await IdsAsync(moderator.Token, $"/moderation/cases?q=конфликт-{t}&pageSize=50"));
        Assert.DoesNotContain(id, await IdsAsync(moderator.Token, $"/moderation/cases?q={t}&pageSize=50"));
    }

    [Fact]
    public async Task Ban_requests_are_searched_by_the_user_the_requester_and_the_reason()
    {
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var t = Tag();
        var (caseId, _, against) = await CaseAsync($"Жалобщик-{t}", $"c-{t}@example.com", $"Нарушитель-{t}", $"bad-{t}@example.com", Text);
        var requested = BanRequest.ByModerator(against.Id, caseId, moderator.Id, $"Систематические оскорбления участников, метка {t}-причина");
        var automatic = BanRequest.Automatic(against.Id, null, $"Три предупреждения за год. Авто-{t}");
        await _factory.QueryAsync(async db =>
        {
            db.BanRequests.AddRange(requested, automatic);
            await db.SaveChangesAsync();
            return 0;
        });

        var byName = await IdsAsync(admin.Token, $"/admin/ban-requests?q=НАРУШИТЕЛЬ-{t}");
        Assert.Equal(2, byName.Count);
        Assert.Contains(requested.Id, byName);
        Assert.Contains(automatic.Id, byName);
        Assert.Equal(2, (await IdsAsync(admin.Token, $"/admin/ban-requests?q=bad-{t}@example")).Count);          // почта того, на кого запрос
        Assert.Equal([requested.Id], await IdsAsync(admin.Token, $"/admin/ban-requests?q={moderator.User.Email!.ToUpperInvariant()}"));   // почта того, кто подал
        Assert.Equal([automatic.Id], await IdsAsync(admin.Token, $"/admin/ban-requests?q=авто-{t}"));              // текст причины
        Assert.Empty(await IdsAsync(admin.Token, $"/admin/ban-requests?q=нет-такого-{t}"));
        Assert.Equal([requested.Id], await IdsAsync(admin.Token, $"/admin/ban-requests?q=нарушитель-{t}&status=Pending&pageSize=1"));   // страница по найденным
        Assert.Equal(2, (await JsonAsync(await GetAsync(admin.Token, $"/admin/ban-requests?q=нарушитель-{t}&pageSize=1"))).GetProperty("totalCount").GetInt32());

        var tooLong = await GetAsync(admin.Token, $"/admin/ban-requests?q={new string('я', 101)}");
        Assert.Equal("invalid_search", (await JsonAsync(tooLong)).GetProperty("code").GetString());
    }
}
