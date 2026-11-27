using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.LiveKit;

/// <summary>
/// POST /dev/seed-lesson и сокращение сроков урока для ручной проверки видеокомнаты. Работает только в Development; в остальных средах маршрута нет (404).
/// Сроки урока общие на процесс, поэтому каждый тест, который их меняет, возвращает значения ТЗ (<see cref="LessonTiming.Reset"/>).
/// </summary>
[Collection(ApiCollection.Name)]
public class LessonSeedDevToolsTests
{
    private static readonly (string Key, string? Value)[] Short =
    [
        ("Lessons:MinLeadMinutes", "1"),
        ("Lessons:FreeChangeMinutes", "1"),
        ("Lessons:JoinOpensBeforeMinutes", "3"),
        ("Lessons:FirstCheckMinutes", "2"),
        ("Lessons:JoinClosesAfterEndMinutes", "3"),
        ("Lessons:MinJointMinutes", "2"),
        ("Lessons:DurationMinutes", "5"),
        ("Lessons:ManualConfirmMinutes", "60"),
    ];

    private static readonly WebApplicationFactoryClientOptions NoRedirect = new() { AllowAutoRedirect = false };

    private readonly ApiFactory _factory;

    public LessonSeedDevToolsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<(string Token, HttpResponseMessage Response)> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        var token = response.IsSuccessStatusCode ? (await JsonAsync(response)).GetProperty("accessToken").GetString()! : string.Empty;
        return (token, response);
    }

    // ---------- необязательные параметры (проверка уведомлений) ----------

    [Fact]
    public async Task The_seed_can_reuse_an_existing_dev_student_and_start_the_lesson_later()
    {
        try
        {
            using var app = _factory.WithSettings("Development", Short);
            var client = app.CreateLocalClient(NoRedirect);
            var first = await JsonAsync(await client.PostAsync("/dev/seed-lesson", null));
            var student = first.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("role").GetString() == "student");

            var response = await client.PostAsJsonAsync("/dev/seed-lesson", new { studentEmail = student.GetProperty("email").GetString(), startsInMinutes = 61 });

            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var body = await JsonAsync(response);
            var again = body.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("role").GetString() == "student");
            Assert.Equal(student.GetProperty("userId").GetGuid(), again.GetProperty("userId").GetGuid());
            Assert.Equal(JsonValueKind.Null, again.GetProperty("password").ValueKind);   // пароль чужого аккаунта не выдаётся
            Assert.Equal(Now.AddMinutes(61), body.GetProperty("startAt").GetDateTime(), TimeSpan.FromSeconds(5));
        }
        finally
        {
            LessonTiming.Reset();
        }
    }

    [Theory]
    [InlineData("nobody@example.com", 5, "seed_student_not_found")]
    [InlineData("dev-nobody@example.com", 5, "seed_student_not_found")]
    [InlineData("dev-admin@gmail.com", 5, "seed_student_not_found")]
    [InlineData(null, 0, "invalid_seed_request")]
    [InlineData(null, 181, "invalid_seed_request")]
    public async Task The_seed_refuses_other_accounts_and_odd_start_times(string? email, int startsIn, string code)
    {
        try
        {
            using var app = _factory.WithSettings("Development", Short);

            var response = await app.CreateLocalClient(NoRedirect).PostAsJsonAsync("/dev/seed-lesson", new { studentEmail = email, startsInMinutes = startsIn });

            Assert.False(response.IsSuccessStatusCode);
            Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
        }
        finally
        {
            LessonTiming.Reset();
        }
    }

    // ---------- сидер ----------

    [Fact]
    public async Task The_seed_creates_two_users_who_can_sign_in_an_exchange_and_a_confirmed_lesson_that_starts_in_two_minutes()
    {
        try
        {
            using var app = _factory.WithSettings("Development", Short);
            var client = app.CreateLocalClient(NoRedirect);

            var response = await client.PostAsync("/dev/seed-lesson", null);

            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString());   // в ответе пароли
            var body = await JsonAsync(response);
            var users = body.GetProperty("users").EnumerateArray().ToList();
            Assert.Equal(2, users.Count);
            Assert.Equal(["teacher", "student"], users.Select(u => u.GetProperty("role").GetString()));
            Assert.All(users, u => Assert.True(u.GetProperty("password").GetString()!.Length >= 12));

            foreach (var user in users)
            {
                var (token, login) = await LoginAsync(client, user.GetProperty("email").GetString()!, user.GetProperty("password").GetString()!);
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);   // email подтверждён, пароль подходит
                Assert.NotEmpty(token);
            }

            var exchangeId = body.GetProperty("exchangeId").GetGuid();
            var sessionId = body.GetProperty("sessionId").GetGuid();
            Assert.Equal($"session-{sessionId}", body.GetProperty("roomName").GetString());
            Assert.Equal(Now.AddMinutes(2), body.GetProperty("startAt").GetDateTime(), TimeSpan.FromSeconds(5));
            Assert.Equal(Now.AddMinutes(7), body.GetProperty("endAt").GetDateTime(), TimeSpan.FromSeconds(5));          // урок 5 минут
            Assert.Equal(Now.AddMinutes(-1), body.GetProperty("joinOpensAt").GetDateTime(), TimeSpan.FromSeconds(5));   // вход открыт: за 3 минуты до начала
            Assert.Equal(Now.AddMinutes(10), body.GetProperty("roomClosesAt").GetDateTime(), TimeSpan.FromSeconds(5));  // закрытие через 3 минуты после окончания
            Assert.EndsWith($"/exchanges/{exchangeId}", body.GetProperty("exchangeUrl").GetString());

            var (exchange, session) = await _factory.QueryAsync(async db => (
                await db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == exchangeId),
                await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId)));
            Assert.Equal(ExchangeStatus.Active, exchange.Status);
            Assert.Equal(SessionStatus.Scheduled, session.Status);
            Assert.Equal(5, session.DurationMin);
            Assert.Equal(exchangeId, session.ExchangeId);

            // Урок можно открыть: учитель получает токен входа в комнату.
            var teacher = users[0];
            var (teacherToken, _) = await LoginAsync(client, teacher.GetProperty("email").GetString()!, teacher.GetProperty("password").GetString()!);
            var join = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/join-token");
            join.Headers.Authorization = new AuthenticationHeaderValue("Bearer", teacherToken);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(join)).StatusCode);
        }
        finally
        {
            LessonTiming.Reset();
        }
    }

    [Fact]
    public async Task Every_call_makes_new_users_and_a_new_lesson()
    {
        try
        {
            using var app = _factory.WithSettings("Development", Short);
            var client = app.CreateLocalClient(NoRedirect);

            var first = await JsonAsync(await client.PostAsync("/dev/seed-lesson", null));
            var second = await JsonAsync(await client.PostAsync("/dev/seed-lesson", null));

            Assert.NotEqual(first.GetProperty("sessionId").GetGuid(), second.GetProperty("sessionId").GetGuid());
            var emails = first.GetProperty("users").EnumerateArray().Concat(second.GetProperty("users").EnumerateArray())
                .Select(u => u.GetProperty("email").GetString()).ToList();
            Assert.Equal(4, emails.Distinct().Count());
        }
        finally
        {
            LessonTiming.Reset();
        }
    }

    [Fact]
    public async Task With_the_specification_timing_the_seed_explains_what_to_change_and_creates_nothing()
    {
        // Фабрика тестов задаёт значения ТЗ даже в Development: слот «через две минуты» при минимуме в час невозможен.
        using var app = _factory.WithSettings("Development");
        var client = app.CreateLocalClient(NoRedirect);
        var before = await _factory.QueryAsync(db => db.Users.CountAsync(u => u.Email!.StartsWith("dev-teacher-")));

        var response = await client.PostAsync("/dev/seed-lesson", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("lesson_timing_not_shortened", body.GetProperty("code").GetString());
        Assert.Contains("MinLeadMinutes", body.GetProperty("title").GetString());
        Assert.Equal(before, await _factory.QueryAsync(db => db.Users.CountAsync(u => u.Email!.StartsWith("dev-teacher-"))));
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task Outside_development_there_is_no_such_route_at_all(string environment)
    {
        using var app = _factory.WithSettings(environment);
        var client = app.CreateLocalClient(NoRedirect);

        var response = await client.PostAsync("/dev/seed-lesson", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_seed_is_not_in_the_api_schema()
    {
        using var app = _factory.WithSettings("Development", Short);
        try
        {
            var schema = await app.CreateLocalClient(NoRedirect).GetStringAsync("/openapi/v1.json");

            Assert.DoesNotContain("seed-lesson", schema);
        }
        finally
        {
            LessonTiming.Reset();
        }
    }

    // ---------- сроки урока ----------

    [Fact]
    public async Task In_development_the_timing_settings_shorten_the_lesson_rules_and_the_next_start_restores_the_specification_values()
    {
        try
        {
            using (var app = _factory.WithSettings("Development", Short))
            {
                _ = app.CreateLocalClient();

                Assert.Equal(TimeSpan.FromMinutes(1), LessonSession.MinLeadTime);
                Assert.Equal(TimeSpan.FromMinutes(3), LessonSession.JoinOpensBefore);
                Assert.Equal(TimeSpan.FromMinutes(2), LessonSession.FirstCheckAfterStart);
                Assert.Equal(TimeSpan.FromMinutes(3), LessonSession.JoinClosesAfterEnd);
                Assert.Equal(2, LessonSession.MinJointMinutes);
                Assert.Equal(5, LessonSession.DurationMinutes);
            }

            using (var again = _factory.WithSettings("Development"))
            {
                _ = again.CreateLocalClient();   // новый запуск без Lessons:* — значения ТЗ (тестовая фабрика задаёт их явно)

                Assert.Equal(TimeSpan.FromHours(1), LessonSession.MinLeadTime);
                Assert.Equal(30, LessonSession.MinJointMinutes);
                Assert.Equal(45, LessonSession.DurationMinutes);
            }
        }
        finally
        {
            LessonTiming.Reset();
        }
    }

    [Fact]
    public void The_committed_development_settings_shorten_the_timing_so_the_seed_works_out_of_the_box()
    {
        var path = FindUp("backend/src/SkillSwap.Api/appsettings.Development.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var lessons = document.RootElement.GetProperty("Lessons");

        Assert.True(lessons.GetProperty("MinLeadMinutes").GetInt32() < 2, "слот «через две минуты» должен быть возможен");
        Assert.True(lessons.GetProperty("MinJointMinutes").GetInt32() <= lessons.GetProperty("DurationMinutes").GetInt32());
        Assert.Equal("ws://localhost:7880", document.RootElement.GetProperty("LiveKit").GetProperty("Url").GetString());
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("Password", text);   // секретов в файле нет
        Assert.DoesNotContain("Secret", text);
    }

    [Theory]
    [InlineData("Lessons:MinJointMinutes", "99")]            // больше длительности урока: засчитать нельзя
    [InlineData("Lessons:DurationMinutes", "0")]
    [InlineData("Lessons:MinLeadMinutes", "-1")]
    [InlineData("Lessons:FirstCheckMinutes", "0")]
    [InlineData("Lessons:FirstCheckMinutes", "500")]
    public void Nonsense_timing_stops_the_application_at_startup(string key, string value)
    {
        try
        {
            using var app = _factory.WithSettings("Development", (key, value));

            var error = Assert.ThrowsAny<Exception>(() => app.CreateLocalClient());

            Assert.Contains("Lessons", error.ToString());
        }
        finally
        {
            LessonTiming.Reset();
        }
    }

    [Fact]
    public void Outside_development_and_testing_only_the_specification_timing_is_accepted()
    {
        try
        {
            using var shortened = _factory.WithSettings("Production", Short);
            var error = Assert.ThrowsAny<Exception>(() => shortened.CreateLocalClient());
            Assert.Contains("Development", error.ToString());

            using var specification = _factory.WithSettings("Production");
            _ = specification.CreateLocalClient();   // значения ТЗ в боевой среде допустимы
        }
        finally
        {
            LessonTiming.Reset();
        }
    }

    private static string FindUp(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
