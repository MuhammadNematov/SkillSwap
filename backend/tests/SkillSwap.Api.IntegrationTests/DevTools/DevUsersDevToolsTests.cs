using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.DevTools;

/// <summary>
/// Постоянные dev-user-1…3 (<c>/dev/seed-users</c>), вход без пароля (<c>/dev/login-user</c>) и сброс урока (<c>/dev/reset-lesson</c>), а также правило для всех маршрутов /dev/*:
/// только Development и только запросы с этого компьютера (loopback, в том числе после разбора X-Forwarded-For), иначе 404.
/// Сроки урока общие на процесс, поэтому тесты с сокращёнными сроками возвращают значения ТЗ (<see cref="LessonTiming.Reset"/>);
/// dev-пользователи в общей базе в конце убираются из подбора, чтобы не попадать в выдачу других тестов.
/// </summary>
[Collection(ApiCollection.Name)]
public class DevUsersDevToolsTests
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

    private static readonly string[] Emails = ["dev-user-1@example.com", "dev-user-2@example.com", "dev-user-3@example.com"];
    private static readonly WebApplicationFactoryClientOptions NoRedirect = new() { AllowAutoRedirect = false };

    private readonly ApiFactory _factory;

    public DevUsersDevToolsTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1" + url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email) =>
        (await JsonAsync(await client.PostAsJsonAsync("/dev/login-user", new { email }))).GetProperty("accessToken").GetString()!;

    /// <summary>Запуск с сокращёнными сроками урока; после — значения ТЗ и dev-пользователи вне подбора.</summary>
    private async Task WithDevelopmentAsync(Func<HttpClient, Task> test)
    {
        try
        {
            using var app = _factory.WithSettings("Development", Short);
            await test(app.CreateLocalClient(NoRedirect));
        }
        finally
        {
            LessonTiming.Reset();
            await _factory.QueryAsync(db => db.Users.Where(u => Emails.Contains(u.Email!)).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsSearchable, false)));
        }
    }

    // ---------- доступ ----------

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task Outside_development_the_routes_do_not_exist(string environment)
    {
        using var app = _factory.WithSettings(environment);
        var client = app.CreateLocalClient(NoRedirect);

        foreach (var route in new[] { "/dev/seed-users", "/dev/login-user", "/dev/reset-lesson" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route, new { email = Emails[0] })).StatusCode);
        }
    }

    [Theory]
    [InlineData("/dev/seed-users")]
    [InlineData("/dev/login-user")]
    [InlineData("/dev/reset-lesson")]
    [InlineData("/dev/seed-staff")]
    [InlineData("/dev/seed-lesson")]
    [InlineData("/dev/staff-code")]
    public async Task In_development_a_request_from_another_computer_gets_404(string route)
    {
        using var app = _factory.WithSettings("Development", Short);
        try
        {
            var remote = app.CreateClient(NoRedirect);   // адрес по умолчанию 203.0.113.7: не этот компьютер
            var countBefore = await _factory.QueryAsync(db => db.Users.CountAsync());

            var response = await remote.PostAsJsonAsync(route, new { email = Emails[0] });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(countBefore, await _factory.QueryAsync(db => db.Users.CountAsync()));   // до обработчика запрос не дошёл
        }
        finally
        {
            LessonTiming.Reset();
        }
    }

    [Fact]
    public async Task A_phone_behind_the_vite_proxy_is_not_local_even_though_the_proxy_is()
    {
        using var app = _factory.WithSettings("Development", Short);
        try
        {
            // Прокси Vite на этом компьютере (loopback) передаёт адрес телефона: после разбора X-Forwarded-For это уже не loopback
            var proxy = app.CreateLocalClient(NoRedirect);
            var request = new HttpRequestMessage(HttpMethod.Post, "/dev/login-user") { Content = JsonContent.Create(new { email = Emails[0] }) };
            request.Headers.Add("X-Forwarded-For", "192.168.0.50");

            Assert.Equal(HttpStatusCode.NotFound, (await proxy.SendAsync(request)).StatusCode);
        }
        finally
        {
            LessonTiming.Reset();
        }
    }

    // ---------- пользователи ----------

    [Fact]
    public Task Seeding_twice_keeps_the_same_users_and_exchange_with_a_full_profile_chat_and_a_lesson() => WithDevelopmentAsync(async client =>
    {
        var first = await JsonAsync(await client.PostAsync("/dev/seed-users", null));
        var second = await JsonAsync(await client.PostAsync("/dev/seed-users", null));

        var ids = first.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("userId").GetGuid()).ToArray();
        Assert.Equal(Emails, first.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("email").GetString()).ToArray());
        Assert.Equal(ids, second.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("userId").GetGuid()).ToArray());
        Assert.Equal(first.GetProperty("exchange").GetProperty("id").GetGuid(), second.GetProperty("exchange").GetProperty("id").GetGuid());
        Assert.False(second.GetProperty("exchange").GetProperty("created").GetBoolean());
        Assert.Equal(first.GetProperty("lesson").GetProperty("sessionId").GetGuid(), second.GetProperty("lesson").GetProperty("sessionId").GetGuid());   // открытый урок не пересоздаётся
        Assert.False(second.GetProperty("lesson").GetProperty("created").GetBoolean());

        foreach (var id in ids)
        {
            var user = await _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));
            Assert.True(user.IsSearchable);
            Assert.NotNull(user.AvatarKey);
            Assert.False(string.IsNullOrWhiteSpace(user.Bio));
            Assert.Equal(["en", "ru", "tg"], await _factory.QueryAsync(db => db.UserLanguages.Where(l => l.UserId == id).Select(l => l.LanguageCode).OrderBy(c => c).ToListAsync()));
            var skills = await _factory.QueryAsync(db => db.UserSkills.Where(s => s.UserId == id).Select(s => s.Direction).ToListAsync());
            Assert.Contains(SkillDirection.Teach, skills);
            Assert.Contains(SkillDirection.Learn, skills);
        }

        var exchangeId = first.GetProperty("exchange").GetProperty("id").GetGuid();
        var texts = await _factory.QueryAsync(db => db.Messages.CountAsync(m => m.ExchangeId == exchangeId && m.Type == MessageType.Text));
        Assert.Equal(3, texts);   // переписка не дублируется при повторном вызове
        var lessonId = first.GetProperty("lesson").GetProperty("sessionId").GetGuid();
        var lesson = await _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == lessonId));
        Assert.Equal(SessionStatus.Scheduled, lesson.Status);
        Assert.True(lesson.RoomClosesAt > Now);   // урок открыт: в него можно войти
        if (first.GetProperty("lesson").GetProperty("created").GetBoolean())
        {
            Assert.Equal(Now.AddMinutes(2), lesson.StartAt, TimeSpan.FromSeconds(5));   // новый урок, как у /dev/seed-lesson (база общая: урок мог остаться от другого теста)
        }
        Assert.Equal("Dev-user-pass-1", first.GetProperty("users")[0].GetProperty("password").GetString());
    });

    [Fact]
    public Task The_users_find_each_other_in_matching_except_the_pair_that_already_has_an_exchange() => WithDevelopmentAsync(async client =>
    {
        var ids = (await JsonAsync(await client.PostAsync("/dev/seed-users", null))).GetProperty("users").EnumerateArray().Select(u => u.GetProperty("userId").GetGuid()).ToArray();

        async Task<List<Guid>> MatchesAsync(string email) =>
            (await JsonAsync(await GetAsync(client, await LoginAsync(client, email), "/matches?limit=50"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetGuid()).ToList();

        var forFirst = await MatchesAsync(Emails[0]);
        var forThird = await MatchesAsync(Emails[2]);

        Assert.Contains(ids[2], forFirst);
        Assert.DoesNotContain(ids[1], forFirst);   // с user-2 уже идёт обмен: повторно не предлагается
        Assert.Contains(ids[0], forThird);
        Assert.Contains(ids[1], forThird);
    });

    [Fact]
    public Task Without_file_storage_the_users_are_still_created_and_the_avatar_waits_for_the_next_call() => WithDevelopmentAsync(async client =>
    {
        await JsonAsync(await client.PostAsync("/dev/seed-users", null));
        await _factory.QueryAsync(db => db.Users.Where(u => u.Email == "dev-user-3@example.com").ExecuteUpdateAsync(s => s.SetProperty(u => u.AvatarKey, (string?)null)));
        Task<string?> AvatarAsync() => _factory.QueryAsync(db => db.Users.Where(u => u.Email == "dev-user-3@example.com").Select(u => u.AvatarKey).SingleAsync());

        // Хранилище не отвечает (например, не запущен Docker): пользователи и обмен есть, аватар пропущен с пояснением
        using (var broken = _factory.WithSettings("Development", [.. Short, ("Storage:Endpoint", "http://127.0.0.1:1")]))
        {
            var body = await JsonAsync(await broken.CreateLocalClient(NoRedirect).PostAsync("/dev/seed-users", null));
            Assert.Contains("docker", body.GetProperty("avatarNote").GetString());
            Assert.Equal(3, body.GetProperty("users").GetArrayLength());
        }

        Assert.Null(await AvatarAsync());

        var again = await JsonAsync(await client.PostAsync("/dev/seed-users", null));   // хранилище снова доступно: аватар загружается
        Assert.Equal(JsonValueKind.Null, again.GetProperty("avatarNote").ValueKind);
        Assert.NotNull(await AvatarAsync());
    });

    // ---------- вход без пароля ----------

    [Fact]
    public Task Login_gives_a_real_session_with_a_refresh_cookie() => WithDevelopmentAsync(async client =>
    {
        await JsonAsync(await client.PostAsync("/dev/seed-users", null));

        var response = await client.PostAsJsonAsync("/dev/login-user", new { email = "DEV-USER-2@example.com" });

        var body = await JsonAsync(response);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("refresh_token=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        var me = await JsonAsync(await GetAsync(client, body.GetProperty("accessToken").GetString()!, "/me"));
        Assert.Equal(Emails[1], me.GetProperty("email").GetString());
    });

    [Theory]
    [InlineData("dev-admin@example.com")]   // сотрудник: им вход без 2FA этим маршрутом не открывается
    [InlineData("dev-user-4@example.com")]
    [InlineData("someone@example.com")]
    [InlineData("")]
    public Task Login_works_only_for_the_three_dev_users(string email) => WithDevelopmentAsync(async client =>
    {
        await client.PostAsync("/dev/seed-staff", null);

        var response = await client.PostAsJsonAsync("/dev/login-user", new { email });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    });

    // ---------- сброс урока ----------

    [Fact]
    public Task Reset_cancels_the_open_lesson_without_blame_and_schedules_a_new_one_in_a_minute() => WithDevelopmentAsync(async client =>
    {
        var seeded = await JsonAsync(await client.PostAsync("/dev/seed-users", null));
        var oldId = seeded.GetProperty("lesson").GetProperty("sessionId").GetGuid();

        var reset = await JsonAsync(await client.PostAsync("/dev/reset-lesson", null));

        var old = await _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == oldId));
        Assert.Equal(SessionStatus.Cancelled, old.Status);
        Assert.Null(old.CancelledById);   // без виновного: на надёжность не влияет
        var newId = reset.GetProperty("lesson").GetProperty("sessionId").GetGuid();
        Assert.NotEqual(oldId, newId);
        var fresh = await _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == newId));
        Assert.Equal(SessionStatus.Scheduled, fresh.Status);
        Assert.Equal(seeded.GetProperty("exchange").GetProperty("id").GetGuid(), fresh.ExchangeId);
        Assert.InRange(fresh.StartAt, Now.AddMinutes(1), Now.AddMinutes(1).AddSeconds(15));

        // Ещё раз подряд: снова ровно один открытый урок
        await JsonAsync(await client.PostAsync("/dev/reset-lesson", null));
        var exchangeId = fresh.ExchangeId;
        Assert.Equal(1, await _factory.QueryAsync(db => db.Sessions.CountAsync(s => s.ExchangeId == exchangeId && (s.Status == SessionStatus.Scheduled || s.Status == SessionStatus.Proposed))));
    });

    [Fact]
    public async Task Reset_without_shortened_lesson_timing_explains_what_to_change()
    {
        using var app = _factory.WithSettings("Development");
        var response = await app.CreateLocalClient(NoRedirect).PostAsync("/dev/reset-lesson", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("lesson_timing_not_shortened", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_routes_are_not_in_the_api_schema()
    {
        using var app = _factory.WithSettings("Development", Short);
        try
        {
            var schema = await app.CreateLocalClient(NoRedirect).GetStringAsync("/openapi/v1.json");
            Assert.DoesNotContain("/dev/", schema);
        }
        finally
        {
            LessonTiming.Reset();
        }
    }
}
