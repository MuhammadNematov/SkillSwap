using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
            Assert.Equal("Dev-staff-pass-1", b.GetProperty(key).GetProperty("password").GetString());   // у всех один известный пароль
            Assert.Equal(a.GetProperty(key).GetProperty("twoFactor").GetProperty("sharedKey").GetString(), b.GetProperty(key).GetProperty("twoFactor").GetProperty("sharedKey").GetString());
            Assert.Equal(JsonValueKind.Null, b.GetProperty(key).GetProperty("twoFactor").GetProperty("recoveryCodes").ValueKind);   // резервные коды показываются один раз
            Assert.Equal(2, b.GetProperty(key).GetProperty("twoFactor").GetProperty("codes").GetArrayLength());
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

    // ---------- 2FA служебных аккаунтов разработки ----------

    private Task ResetTotpStepsAsync() => _factory.QueryAsync(async db =>
    {
        // Часы тестов заморожены, а использованный шаг кода запоминается: перед каждой проверкой возвращаем его в начало.
        await db.Users.Where(u => u.Email!.StartsWith("dev-")).ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorLastUsedStep, 0L));
        return 0;
    });

    private static Task<HttpResponseMessage> InviteAsync(HttpClient client, string adminToken, string email, string? code) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/staff-invitations")
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", adminToken) },
            Content = JsonContent.Create(new { email, role = "Moderator", confirmation = new { password = "Dev-staff-pass-1", code } }),
        });

    [Fact]
    public async Task The_seeded_admin_can_confirm_real_actions_with_the_given_codes_and_each_code_works_once()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateClient(NoRedirect);
        await client.PostAsync("/dev/seed-staff", null);
        await ResetTotpStepsAsync();
        var seeded = await JsonAsync(await client.PostAsync("/dev/seed-staff", null));
        var admin = seeded.GetProperty("admin");
        var token = admin.GetProperty("accessToken").GetString()!;
        var codes = admin.GetProperty("twoFactor").GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();

        var first = await InviteAsync(client, token, $"seed-invitee-{Guid.NewGuid():N}@example.com", codes[0]);
        var second = await InviteAsync(client, token, $"seed-invitee-{Guid.NewGuid():N}@example.com", codes[1]);
        var replay = await InviteAsync(client, token, $"seed-invitee-{Guid.NewGuid():N}@example.com", codes[0]);

        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);   // следующий код годится сразу
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal("invalid_two_factor_code", (await JsonAsync(replay)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_staff_code_route_gives_the_current_and_the_next_code_that_confirm_actions()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateClient(NoRedirect);
        var seeded = await JsonAsync(await client.PostAsync("/dev/seed-staff", null));
        await ResetTotpStepsAsync();
        var token = seeded.GetProperty("admin").GetProperty("accessToken").GetString()!;

        var response = await client.PostAsJsonAsync("/dev/staff-code", new { email = "dev-admin@example.com" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        var body = await JsonAsync(response);
        var codes = body.GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(seeded.GetProperty("admin").GetProperty("twoFactor").GetProperty("codes")[0].GetString(), codes[0]);
        Assert.InRange(body.GetProperty("secondsLeft").GetInt32(), 1, 30);
        Assert.Equal(HttpStatusCode.Created, (await InviteAsync(client, token, $"seed-invitee-{Guid.NewGuid():N}@example.com", codes[0])).StatusCode);
    }

    [Theory]
    [InlineData("nobody@example.com")]
    [InlineData("")]
    [InlineData(null)]
    public async Task The_staff_code_route_refuses_unknown_addresses(string? email)
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateClient(NoRedirect);

        var response = await client.PostAsJsonAsync("/dev/staff-code", new { email });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("staff_not_found", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_staff_code_route_does_not_hand_out_codes_of_regular_users()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateClient(NoRedirect);
        var seeded = await JsonAsync(await client.PostAsync("/dev/seed-case", JsonContent.Create(new { })));

        var response = await client.PostAsJsonAsync("/dev/staff-code", new { email = seeded.GetProperty("opener").GetProperty("email").GetString() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_account_from_the_older_seeder_is_brought_to_the_known_password_and_two_factor()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateClient(NoRedirect);
        await client.PostAsync("/dev/seed-staff", null);
        using (var scope = _factory.Services.CreateScope())
        {
            // Как у аккаунта, созданного прежней версией сидера: случайный пароль, 2FA нет.
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<User>>();
            var legacy = (await users.FindByEmailAsync("dev-moderator-2@example.com"))!;
            await users.RemovePasswordAsync(legacy);
            await users.AddPasswordAsync(legacy, "Random-legacy-pass-9");
            await users.SetTwoFactorEnabledAsync(legacy, false);
        }

        var seeded = await JsonAsync(await client.PostAsync("/dev/seed-staff", null));

        var second = seeded.GetProperty("secondModerator");
        Assert.Equal("Dev-staff-pass-1", second.GetProperty("password").GetString());
        Assert.Equal(JsonValueKind.Array, second.GetProperty("twoFactor").GetProperty("recoveryCodes").ValueKind);   // 2FA включена заново, резервные коды выданы
        Assert.Equal(8, second.GetProperty("twoFactor").GetProperty("recoveryCodes").GetArrayLength());
        Assert.True(await _factory.QueryAsync(db => db.Users.AsNoTracking().Where(u => u.Email == "dev-moderator-2@example.com").Select(u => u.TwoFactorEnabled).SingleAsync()));
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

    // ---------- GET /dev/staff-qr ----------

    [Fact]
    public async Task The_staff_qr_page_lists_every_dev_staff_account_with_its_real_totp_key_and_a_qr_code()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateClient(NoRedirect);
        var seeded = await JsonAsync(await client.PostAsync("/dev/seed-staff", null));

        var response = await client.GetAsync("/dev/staff-qr");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(html, "<svg").Count);   // по QR-коду на каждого
        foreach (var (property, email) in new[] { ("moderator", "dev-moderator@example.com"), ("secondModerator", "dev-moderator-2@example.com"), ("admin", "dev-admin@example.com"), ("superAdmin", "dev-superadmin@example.com") })
        {
            var key = seeded.GetProperty(property).GetProperty("twoFactor").GetProperty("sharedKey").GetString()!;
            Assert.Contains($"data-email=\"{email}\"", html);
            Assert.Contains(string.Join(' ', Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4)))), html);   // ключ из базы, а не выдуманный
            Assert.Contains($"otpauth://totp/SkillSwap:{Uri.EscapeDataString(email)}?secret={key}", System.Net.WebUtility.HtmlDecode(html));
        }

        Assert.Contains(SkillSwap.Api.DevTools.CaseSeedDevTools.StaffPassword, html);
    }

    [Fact]
    public async Task The_staff_qr_page_creates_missing_accounts_and_shows_the_same_keys_every_time()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateClient(NoRedirect);

        var first = await (await client.GetAsync("/dev/staff-qr")).Content.ReadAsStringAsync();   // аккаунты могли ещё не существовать
        var second = await (await client.GetAsync("/dev/staff-qr")).Content.ReadAsStringAsync();
        var codeOfAdmin = await JsonAsync(await client.PostAsJsonAsync("/dev/staff-code", new { email = "dev-admin@example.com" }));   // 2FA включена: коды выдаются

        string Keys(string html) => string.Concat(System.Text.RegularExpressions.Regex.Matches(html, "class=\"key\">([^<]+)<").Select(m => m.Groups[1].Value));
        Assert.Equal(Keys(first), Keys(second));
        Assert.Equal(2, codeOfAdmin.GetProperty("codes").GetArrayLength());
    }

    [Fact]
    public async Task The_staff_qr_page_does_not_exist_outside_development()
    {
        var response = await _factory.CreateClient(NoRedirect).GetAsync("/dev/staff-qr");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        foreach (var environment in new[] { "Production", "Staging" })
        {
            using var app = _factory.WithSettings(environment);
            Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient(NoRedirect).GetAsync("/dev/staff-qr")).StatusCode);
        }
    }

    [Theory]
    [InlineData("/dev/seed-staff")]
    [InlineData("/dev/seed-case")]
    [InlineData("/dev/staff-code")]
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
