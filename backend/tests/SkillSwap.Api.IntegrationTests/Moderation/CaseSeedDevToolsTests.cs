using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>
/// <c>POST /dev/seed-staff</c> и <c>POST /dev/seed-case</c> и сокращённые сроки обращений (секция Cases). Работают только в Development; в остальных средах маршрутов нет.
/// Сроки обращений общие на процесс, поэтому каждый тест, который их меняет, возвращает значения ТЗ (<see cref="CaseTiming.Reset"/>).
/// </summary>
[Collection(ApiCollection.Name)]
public class CaseSeedDevToolsTests
{
    private static readonly (string Key, string? Value)[] Short =
    [
        ("Cases:HighSlaMinutes", "3"),
        ("Cases:NormalSlaMinutes", "5"),
        ("Cases:ExplanationWindowMinutes", "4"),
        ("Cases:ExplanationReminderBeforeMinutes", "2"),
        ("Cases:AppealWindowMinutes", "6"),
    ];

    private static readonly WebApplicationFactoryClientOptions NoRedirect = new() { AllowAutoRedirect = false };

    private readonly ApiFactory _factory;

    public CaseSeedDevToolsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1" + url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SeedAsync(HttpClient client, object? body = null) => client.PostAsJsonAsync("/dev/seed-case", body ?? new { });

    private Task<Case> CaseAsync(Guid id) => _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(c => c.Id == id));

    // ---------- служебные аккаунты ----------

    [Fact]
    public async Task The_staff_seed_creates_the_accounts_once_and_hands_out_working_tokens()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateClient(NoRedirect);

        var first = await client.PostAsync("/dev/seed-staff", null);
        var second = await client.PostAsync("/dev/seed-staff", null);

        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        Assert.Contains("no-store", first.Headers.CacheControl?.ToString());   // в ответе пароли и токены
        var a = await JsonAsync(first);
        var b = await JsonAsync(second);
        foreach (var key in new[] { "moderator", "secondModerator", "admin" })
        {
            Assert.Equal(a.GetProperty(key).GetProperty("userId").GetGuid(), b.GetProperty(key).GetProperty("userId").GetGuid());   // аккаунты создаются один раз
            Assert.Equal(JsonValueKind.Null, b.GetProperty(key).GetProperty("password").ValueKind);   // пароль виден только при создании
        }

        Assert.Equal("Moderator", a.GetProperty("moderator").GetProperty("role").GetString());
        Assert.Equal("Admin", a.GetProperty("admin").GetProperty("role").GetString());
        var moderatorToken = b.GetProperty("moderator").GetProperty("accessToken").GetString()!;
        var adminToken = b.GetProperty("admin").GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, moderatorToken, "/moderation/cases")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, adminToken, "/admin/cases")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, moderatorToken, "/admin/cases")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, adminToken, "/moderation/cases")).StatusCode);
    }

    // ---------- обращение на каждой стадии ----------

    [Theory]
    [InlineData("Open", CaseStatus.Open)]
    [InlineData("Taken", CaseStatus.InProgress)]
    [InlineData("Waiting", CaseStatus.WaitingResponse)]
    [InlineData("Resolved", CaseStatus.Resolved)]
    [InlineData("Appealed", CaseStatus.Appealed)]
    [InlineData("AppealReview", CaseStatus.AppealReview)]
    public async Task The_case_seed_brings_a_conduct_case_to_the_requested_stage_through_the_real_services(string stage, CaseStatus expected)
    {
        try
        {
            using var app = _factory.WithSettings("Development", Short);
            var client = app.CreateClient(NoRedirect);

            var response = await SeedAsync(client, new { type = "Conduct", stage });

            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var body = await JsonAsync(response);
            var caseId = body.GetProperty("caseId").GetGuid();
            Assert.Equal(expected, (await CaseAsync(caseId)).Status);
            Assert.EndsWith($"/cases/{caseId}", body.GetProperty("caseUrl").GetString());
            Assert.Equal(2, body.GetProperty("evidenceMessageIds").GetArrayLength());

            // Все участники получили настоящие токены.
            var openerToken = body.GetProperty("opener").GetProperty("accessToken").GetString()!;
            var mine = await JsonAsync(await GetAsync(client, openerToken, "/me/cases"));
            Assert.Equal(caseId, mine.GetProperty("items")[0].GetProperty("id").GetGuid());
            var moderatorToken = body.GetProperty("moderator").GetProperty("accessToken").GetString()!;
            var card = await GetAsync(client, moderatorToken, $"/moderation/cases/{caseId}");
            Assert.Equal(HttpStatusCode.OK, card.StatusCode);   // первый модератор видит своё обращение на любой стадии
            if (stage == "AppealReview")
            {
                Assert.Equal(JsonValueKind.Object, body.GetProperty("secondModerator").ValueKind);
                var reviewerToken = body.GetProperty("secondModerator").GetProperty("accessToken").GetString()!;
                var reviewerCard = await JsonAsync(await GetAsync(client, reviewerToken, $"/moderation/cases/{caseId}"));
                Assert.True(reviewerCard.GetProperty("hasChatAccess").GetBoolean());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, body.GetProperty("secondModerator").ValueKind);
            }

            if (stage is "Resolved" or "Appealed" or "AppealReview")
            {
                var accusedToken = body.GetProperty("accused").GetProperty("accessToken").GetString()!;
                var seen = await JsonAsync(await GetAsync(client, accusedToken, $"/cases/{caseId}"));
                Assert.Equal("Warning", seen.GetProperty("decision").GetString());
            }

            // Обычные пользователи входят по почте и паролю из ответа.
            var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
            {
                email = body.GetProperty("accused").GetProperty("email").GetString(),
                password = body.GetProperty("accused").GetProperty("password").GetString(),
            });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        finally
        {
            CaseTiming.Reset();
        }
    }

    [Fact]
    public async Task A_waiting_case_gets_questions_for_both_sides_and_a_shortened_deadline()
    {
        try
        {
            using var app = _factory.WithSettings("Development", Short);
            var client = app.CreateClient(NoRedirect);

            var body = await JsonAsync(await SeedAsync(client, new { stage = "Waiting" }));

            var stored = await CaseAsync(body.GetProperty("caseId").GetGuid());
            Assert.Equal(Now.AddMinutes(4), stored.ResponseDeadlineAt!.Value, TimeSpan.FromSeconds(10));   // ExplanationWindowMinutes
            Assert.Equal(Now.AddMinutes(3), stored.DueAt, TimeSpan.FromSeconds(10));                       // высокий приоритет: HighSlaMinutes
            var timers = body.GetProperty("timers");
            Assert.True(timers.GetProperty("shortened").GetBoolean());
            Assert.Equal(4, timers.GetProperty("explanationWindowMinutes").GetDouble());
            Assert.Equal(2, timers.GetProperty("explanationReminderBeforeMinutes").GetDouble());
            Assert.Equal(6, timers.GetProperty("appealWindowMinutes").GetDouble());
            foreach (var who in new[] { "opener", "accused" })
            {
                var thread = await JsonAsync(await GetAsync(client, body.GetProperty(who).GetProperty("accessToken").GetString()!, $"/cases/{stored.Id}/explanations"));
                Assert.Equal(1, thread.GetProperty("items").GetArrayLength());
            }
        }
        finally
        {
            CaseTiming.Reset();
        }
    }

    [Fact]
    public async Task The_appeal_window_follows_the_shortened_setting()
    {
        try
        {
            using var app = _factory.WithSettings("Development", Short);
            var client = app.CreateClient(NoRedirect);

            var body = await JsonAsync(await SeedAsync(client, new { stage = "Resolved" }));

            var jobs = (SkillSwap.Infrastructure.Jobs.InlineBackgroundJobs)app.Services.GetService(typeof(SkillSwap.Application.Abstractions.IBackgroundJobs))!;
            var closing = jobs.Scheduled.Where(j => j.Description.Contains("CloseIfDueAsync")).OrderByDescending(j => j.RunAt).First();
            Assert.Equal(Now.AddMinutes(6), closing.RunAt.UtcDateTime, TimeSpan.FromSeconds(10));   // закрытие через AppealWindowMinutes
            Assert.NotEqual(Guid.Empty, body.GetProperty("caseId").GetGuid());
        }
        finally
        {
            CaseTiming.Reset();
        }
    }

    [Fact]
    public async Task A_review_dispute_seed_publishes_the_reviews_and_the_hidden_decision_takes_effect()
    {
        try
        {
            using var app = _factory.WithSettings("Development", Short);
            var client = app.CreateClient(NoRedirect);

            var body = await JsonAsync(await SeedAsync(client, new { type = "ReviewDispute", stage = "Resolved" }));

            var reviewId = body.GetProperty("reviewId").GetGuid();
            var review = await _factory.QueryAsync(db => db.Reviews.AsNoTracking().SingleAsync(r => r.Id == reviewId));
            Assert.Equal(ReviewStatus.Hidden, review.Status);   // решение по умолчанию для спора: ReviewHidden
            Assert.Equal(0, body.GetProperty("evidenceMessageIds").GetArrayLength());
            var dto = await JsonAsync(await GetAsync(client, body.GetProperty("opener").GetProperty("accessToken").GetString()!, $"/cases/{body.GetProperty("caseId").GetGuid()}"));
            Assert.Equal("ReviewDispute", dto.GetProperty("type").GetString());
            Assert.Equal("ReviewHidden", dto.GetProperty("decision").GetString());
        }
        finally
        {
            CaseTiming.Reset();
        }
    }

    [Fact]
    public async Task The_decision_can_be_chosen_and_must_fit_the_type()
    {
        try
        {
            using var app = _factory.WithSettings("Development", Short);
            var client = app.CreateClient(NoRedirect);

            var ended = await JsonAsync(await SeedAsync(client, new { stage = "Resolved", decision = "ExchangeEnded" }));
            var wrong = await SeedAsync(client, new { stage = "Resolved", decision = "ReviewKept" });   // для жалобы не подходит

            var stored = await CaseAsync(ended.GetProperty("caseId").GetGuid());
            Assert.Equal(CaseDecision.ExchangeEnded, stored.Decision);
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal("invalid_seed_decision", (await JsonAsync(wrong)).GetProperty("code").GetString());
        }
        finally
        {
            CaseTiming.Reset();
        }
    }

    [Theory]
    [InlineData("""{"type":"Unknown"}""", "invalid_seed_type")]
    [InlineData("""{"type":"5"}""", "invalid_seed_type")]
    [InlineData("""{"stage":"Nowhere"}""", "invalid_seed_stage")]
    public async Task A_bad_request_is_refused_with_a_clear_code_and_creates_nothing(string json, string code)
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateClient(NoRedirect);
        var before = await _factory.QueryAsync(db => db.Users.CountAsync(u => u.Email!.StartsWith("dev-opener-")));

        var response = await client.PostAsync("/dev/seed-case", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(before, await _factory.QueryAsync(db => db.Users.CountAsync(u => u.Email!.StartsWith("dev-opener-"))));
    }

    [Fact]
    public async Task Every_call_makes_new_users_and_a_new_case_but_reuses_the_staff()
    {
        try
        {
            using var app = _factory.WithSettings("Development", Short);
            var client = app.CreateClient(NoRedirect);

            var first = await JsonAsync(await SeedAsync(client));
            var second = await JsonAsync(await SeedAsync(client));

            Assert.NotEqual(first.GetProperty("caseId").GetGuid(), second.GetProperty("caseId").GetGuid());
            Assert.NotEqual(first.GetProperty("opener").GetProperty("userId").GetGuid(), second.GetProperty("opener").GetProperty("userId").GetGuid());
            Assert.Equal(first.GetProperty("moderator").GetProperty("userId").GetGuid(), second.GetProperty("moderator").GetProperty("userId").GetGuid());
        }
        finally
        {
            CaseTiming.Reset();
        }
    }

    // ---------- вне Development ----------

    [Theory]
    [InlineData("/dev/seed-staff")]
    [InlineData("/dev/seed-case")]
    public async Task The_seed_routes_do_not_exist_outside_development(string url)
    {
        var client = _factory.CreateClient(NoRedirect);

        var response = await client.PostAsync(url, null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Shortened_case_timers_are_refused_outside_development_and_testing_and_do_not_leak_between_hosts()
    {
        using (var shortened = _factory.WithSettings("Production", Short))
        {
            var error = Assert.ThrowsAny<Exception>(() => shortened.CreateClient());
            Assert.Contains("Cases", error.ToString());
        }

        using (var broken = _factory.WithSettings("Development", ("Cases:ExplanationWindowMinutes", "5"), ("Cases:ExplanationReminderBeforeMinutes", "9")))
        {
            Assert.ThrowsAny<Exception>(() => broken.CreateClient());   // напоминание позже конца срока: недопустимо
        }

        using (var specification = _factory.WithSettings("Production"))
        {
            specification.CreateClient();   // значения ТЗ в боевой среде допустимы
        }

        Assert.Equal(CaseTiming.Defaults, CaseTiming.Current);
        Assert.Equal(TimeSpan.FromDays(7), CaseRules.AppealWindow);
    }

    [Fact]
    public async Task The_development_host_of_a_test_with_the_factory_defaults_keeps_the_timers_of_the_specification()
    {
        // Фабрика тестов задаёт значения ТЗ и в Development: сокращённые из appsettings.Development.json не подхватываются, другие тесты не страдают.
        using var app = _factory.WithSettings("Development");
        app.CreateClient();

        Assert.Equal(CaseTiming.Defaults, CaseTiming.Current);
        await Task.CompletedTask;
    }
}
