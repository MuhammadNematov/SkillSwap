using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using Xunit.Abstractions;

namespace SkillSwap.Api.IntegrationTests.Matching;

/// <summary>
/// Раздел 14 ТЗ: подбор укладывается в p95 ≤ 300 мс. Замер идёт на 5000 пользователей с плотными пересечениями навыков
/// (худший случай: кандидатов в выдаче — тысячи), через настоящее приложение и настоящий PostgreSQL.
/// Отдельный контейнер БД: данные замера не попадают в остальные тесты.
/// </summary>
public class MatchPerformanceTests : IClassFixture<ApiFactory>
{
    // По умолчанию 5000 (раздел 14 ТЗ: «несколько тысяч»). Для проверки запаса: SKILLSWAP_PERF_USERS=20000.
    private static readonly int Users = int.TryParse(Environment.GetEnvironmentVariable("SKILLSWAP_PERF_USERS"), out var n) && n > 0 ? n : 5000;
    private const int SkillCount = 20;
    private const int Requests = 60;
    private const double P95LimitMs = 300;

    private static readonly string[] Zones =
        ["Asia/Dushanbe", "Asia/Tashkent", "Europe/Moscow", "Europe/Berlin", "Europe/London", "America/New_York", "Asia/Tokyo", "Asia/Dhaka", "Asia/Almaty", "Europe/Istanbul"];

    private static readonly string[] Languages = ["tg", "ru", "en", "uz", "de", "fa", "tr", "zh"];

    private readonly ApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public MatchPerformanceTests(ApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task Matching_over_five_thousand_users_stays_within_the_p95_limit()
    {
        var random = new Random(20261118);
        var (categoryId, skills) = await _factory.QueryAsync(async db =>
        {
            var category = SkillCategory.Create("Perf", "Perf", "Perf", 1);
            db.SkillCategories.Add(category);
            var list = Enumerable.Range(0, SkillCount).Select(i => Skill.Create(category.Id, $"P{i}", $"P{i}", $"P{i}")).ToList();
            db.Skills.AddRange(list);
            await db.SaveChangesAsync();
            return (category.Id, list.Select(s => s.Id).ToArray());
        });

        var viewer = await SeedAsync(random, skills);

        // На боевой БД статистику таблиц обновляет autovacuum; после массовой вставки в тесте её нет, и планировщик слеп.
        // Делаем то же, что сделал бы autovacuum.
        await _factory.QueryAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("ANALYZE");
            return 0;
        });

        var token = TestTokens.ForUser(_factory, viewer);
        var client = _factory.CreateClient();
        var queries = new[]
        {
            "",
            "?pageSize=50",
            "?pageSize=50&page=3",
            "?lang=ru",
            "?level=Intermediate",
            "?minRating=3.5",
            $"?skill={skills[0]}",
            "?lang=en&level=Advanced&minRating=2&pageSize=50",
        };

        async Task<(double Ms, int Total)> Call(string query)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/matches" + query);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var watch = Stopwatch.StartNew();
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            watch.Stop();
            Assert.True(response.IsSuccessStatusCode, body);
            return (watch.Elapsed.TotalMilliseconds, JsonDocument.Parse(body).RootElement.GetProperty("totalCount").GetInt32());
        }

        foreach (var query in queries)
        {
            await Call(query);   // прогрев: компиляция запросов, JIT, кэш планов
        }

        await DiagnoseAsync(viewer, skills);

        var timings = new List<double>();
        var totals = new List<int>();
        for (var i = 0; i < Requests; i++)
        {
            var (ms, total) = await Call(queries[i % queries.Length]);
            timings.Add(ms);
            totals.Add(total);
        }

        timings.Sort();
        var p50 = timings[timings.Count / 2];
        var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
        _output.WriteLine($"Users: {Users}, candidates without filters: {totals[0]}, p50 = {p50:F0} ms, p95 = {p95:F0} ms, max = {timings[^1]:F0} ms");

        Assert.True(totals[0] > 1000, $"в выдаче без фильтров должно быть тысячи кандидатов, а их {totals[0]}: замер не показателен");
        Assert.True(p95 <= P95LimitMs, $"p95 = {p95:F0} мс при пределе {P95LimitMs} мс (p50 = {p50:F0} мс, max = {timings[^1]:F0} мс)");
    }

    /// <summary>Разбивка времени по этапам: нужна, когда замер не укладывается в предел.</summary>
    private async Task DiagnoseAsync(User viewer, Guid[] skills)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SkillSwap.Infrastructure.Persistence.AppDbContext>();
        var mine = await db.UserSkills.AsNoTracking().Where(s => s.UserId == viewer.Id).ToListAsync();
        var seeker = new SkillSwap.Application.Matching.MatchSeeker(
            viewer.Id, false, DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-18), true,
            mine.Where(s => s.Direction == SkillDirection.Learn).Select(s => new SkillSwap.Application.Matching.LeveledSkill(s.SkillId, s.Level)).ToList(),
            mine.Where(s => s.Direction == SkillDirection.Teach).Select(s => new SkillSwap.Application.Matching.LeveledSkill(s.SkillId, s.Level)).ToList());
        var filter = new SkillSwap.Application.Matching.MatchFilter(null, null, null, null);
        var store = new SkillSwap.Infrastructure.Matching.MatchStore(db, TimeProvider.System);

        var watch = Stopwatch.StartNew();
        var rows = await store.FindCandidatesAsync(seeker, filter, CancellationToken.None);
        var sqlMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var ranked = SkillSwap.Application.Matching.MatchRanking.Order(rows, "Asia/Dushanbe", _factory.Clock.GetUtcNow());
        var rankMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        await store.LoadDetailsAsync(seeker, ranked.Take(20).Select(r => r.Candidate.UserId).ToList(), CancellationToken.None);
        var detailsMs = watch.Elapsed.TotalMilliseconds;
        _output.WriteLine($"Этапы: SQL кандидатов {sqlMs:F0} мс ({rows.Count} строк), сортировка {rankMs:F0} мс, подробности страницы {detailsMs:F0} мс");
    }

    private async Task<User> SeedAsync(Random random, Guid[] skills)
    {
        var today = DateOnly.FromDateTime(_factory.Clock.GetUtcNow().UtcDateTime);
        User? viewer = null;

        for (var batch = 0; batch < Users; batch += 500)
        {
            var created = await _factory.QueryAsync(async db =>
            {
                db.ChangeTracker.AutoDetectChangesEnabled = false;
                User? first = null;
                for (var i = batch; i < Math.Min(batch + 500, Users); i++)
                {
                    var teen = random.Next(10) == 0;
                    var birth = teen ? today.AddYears(-17).AddDays(-random.Next(300)) : today.AddYears(-18 - random.Next(30)).AddDays(-1);
                    var user = User.CreateRegular(AuthApi.NewEmail(), "Perf" + i, birth, "en");
                    var searchable = i == 0 || random.Next(10) != 0;
                    user.UpdateProfile("Perf" + i, null, "en", Zones[random.Next(Zones.Length)], searchable);
                    user.LastActiveAt = random.Next(3) == 0 ? null : _factory.Clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(random.Next(24 * 40));
                    if (random.Next(2) == 0)
                    {
                        var count = random.Next(1, 40);
                        typeof(User).GetProperty(nameof(User.RatingCount))!.SetValue(user, count);
                        typeof(User).GetProperty(nameof(User.RatingAvg))!.SetValue(user, Math.Round(1m + (decimal)random.NextDouble() * 4m, 2));
                    }

                    db.Users.Add(user);

                    // По 3 навыка «Умею» и «Хочу изучать» из 20: пересечения плотные, как в худшем случае.
                    var picks = skills.OrderBy(_ => random.Next()).Take(6).ToArray();
                    for (var k = 0; k < 6; k++)
                    {
                        db.UserSkills.Add(UserSkill.Create(
                            user.Id, picks[k], k < 3 ? SkillDirection.Teach : SkillDirection.Learn, (SkillLevel)random.Next(1, 4), null));
                    }

                    foreach (var language in Languages.OrderBy(_ => random.Next()).Take(random.Next(1, 4)))
                    {
                        db.UserLanguages.Add(UserLanguage.Create(user.Id, language));
                    }

                    first ??= user;
                }

                await db.SaveChangesAsync();
                return first!;
            });
            viewer ??= created;
        }

        // Тот, кто ищет: взрослый, самый «неудобный» запрос — много своих и чужих пересечений.
        await _factory.QueryAsync(async db =>
        {
            var me = await db.Users.SingleAsync(u => u.Id == viewer!.Id);
            typeof(User).GetProperty(nameof(User.DateOfBirth))!.SetValue(me, (DateOnly?)today.AddYears(-30));

            // Общий язык обязателен, поэтому ищущий знает все восемь языков: худший случай, ни один кандидат не отсекается по языку.
            db.UserLanguages.RemoveRange(await db.UserLanguages.Where(l => l.UserId == me.Id).ToListAsync());
            foreach (var language in Languages)
            {
                db.UserLanguages.Add(UserLanguage.Create(me.Id, language));
            }

            await db.SaveChangesAsync();
            return 0;
        });
        return viewer!;
    }
}
