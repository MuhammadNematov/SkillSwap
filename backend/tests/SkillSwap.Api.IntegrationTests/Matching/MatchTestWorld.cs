using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Matching;

public sealed record MatchPerson(User User, string Token)
{
    public Guid Id => User.Id;
}

/// <summary>
/// Свой набор навыков на каждый тест, чтобы пользователи из других тестов (в общей БД) не попадали в выдачу, и быстрое
/// создание пользователей прямо в БД (без регистрации и входа).
/// </summary>
public sealed class MatchTestWorld
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    private MatchTestWorld(ApiFactory factory, Guid categoryId, Guid[] skills)
    {
        _factory = factory;
        _client = factory.CreateClient();
        CategoryId = categoryId;
        Skills = skills;
    }

    public Guid CategoryId { get; }

    /// <summary>Шесть навыков этого мира: S(0)…S(5).</summary>
    public Guid[] Skills { get; }

    public Guid S(int index) => Skills[index];

    public static async Task<MatchTestWorld> CreateAsync(ApiFactory factory)
    {
        var marker = "W" + Guid.NewGuid().ToString("N")[..10];
        var (categoryId, skills) = await factory.QueryAsync(async db =>
        {
            var category = SkillCategory.Create(marker, marker, marker, 900);
            db.SkillCategories.Add(category);
            var list = Enumerable.Range(0, 6).Select(i => Skill.Create(category.Id, $"{marker}-{i}", $"{marker}-{i}", $"{marker}-{i}")).ToList();
            db.Skills.AddRange(list);
            await db.SaveChangesAsync();
            return (category.Id, list.Select(s => s.Id).ToArray());
        });
        return new MatchTestWorld(factory, categoryId, skills);
    }

    public sealed record Options(
        string[]? Languages = null,   // по умолчанию ["en"]: общий язык нужен для попадания в выдачу; [] — язык не указан
        string TimeZone = "Asia/Dushanbe",
        DateOnly? BirthDate = null,
        bool Searchable = true,
        int RatingCount = 0,
        decimal Rating = 0m,
        DateTime? LastActiveAt = null,
        string? Name = null,
        UserStatus Status = UserStatus.Active);

    /// <summary>Пользователь с анкетой: teach и learn — пары (номер навыка в мире, уровень).</summary>
    public async Task<MatchPerson> UserAsync(
        (int Skill, SkillLevel Level)[] teach, (int Skill, SkillLevel Level)[] learn, Options? options = null)
    {
        options ??= new Options();
        var user = User.CreateRegular(AuthApi.NewEmail(), options.Name ?? "Кандидат", options.BirthDate ?? new DateOnly(1995, 1, 1), "en");
        user.UpdateProfile(options.Name ?? "Кандидат", null, "en", options.TimeZone, options.Searchable);
        user.LastActiveAt = options.LastActiveAt;
        Set(user, nameof(User.RatingCount), options.RatingCount);
        Set(user, nameof(User.RatingAvg), options.Rating);
        Set(user, nameof(User.Status), options.Status);

        await _factory.QueryAsync(async db =>
        {
            db.Users.Add(user);
            foreach (var (skill, level) in teach)
            {
                db.UserSkills.Add(UserSkill.Create(user.Id, S(skill), SkillDirection.Teach, level, null));
            }

            foreach (var (skill, level) in learn)
            {
                db.UserSkills.Add(UserSkill.Create(user.Id, S(skill), SkillDirection.Learn, level, null));
            }

            foreach (var language in options.Languages ?? ["en"])
            {
                db.UserLanguages.Add(UserLanguage.Create(user.Id, language));
            }

            await db.SaveChangesAsync();
            return 0;
        });

        return new MatchPerson(user, TestTokens.ForUser(_factory, user));
    }

    public Task<MatchPerson> UserAsync(
        (int Skill, SkillLevel Level) teach, (int Skill, SkillLevel Level) learn, Options? options = null) =>
        UserAsync([teach], [learn], options);

    private static void Set<T>(User user, string property, T value) => typeof(User).GetProperty(property)!.SetValue(user, value);

    // ---------- запросы ----------

    public async Task<HttpResponseMessage> GetRawAsync(MatchPerson viewer, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/matches" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer.Token);
        return await _client.SendAsync(request);
    }

    public async Task<JsonElement> GetAsync(MatchPerson viewer, string query = "")
    {
        var response = await GetRawAsync(viewer, query);
        Assert.True(response.IsSuccessStatusCode, $"GET /matches{query} → {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<List<Guid>> IdsAsync(MatchPerson viewer, string query = "") =>
        (await GetAsync(viewer, query)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetGuid()).ToList();

    public Task ModifyAsync(Func<SkillSwap.Infrastructure.Persistence.AppDbContext, Task> change) => _factory.QueryAsync(async db =>
    {
        await change(db);
        await db.SaveChangesAsync();
        return 0;
    });
}
