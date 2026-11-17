using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence.Seed;

namespace SkillSwap.Api.IntegrationTests.Profile;

/// <summary>Профиль (раздел 5.2 ТЗ): GET/PUT /me и публичный GET /users/{id} на настоящей БД.</summary>
[Collection(ApiCollection.Name)]
public class ProfileTests
{
    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public ProfileTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    private sealed record Person(string Token, Guid Id, string Email);

    /// <param name="anketa">Заполнить анкету (по навыку в каждом направлении): без неё нельзя показываться в подборе.</param>
    private async Task<Person> NewPersonAsync(bool anketa = true)
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var token = (await _api.LoginSessionAsync(email)).AccessToken;
        var me = await (await SendAsync(token, HttpMethod.Get, "/me")).Content.ReadFromJsonAsync<JsonElement>();
        if (anketa)
        {
            var put = await SendAsync(token, HttpMethod.Put, "/me/skills", new
            {
                teach = new[] { new { skillId = SkillCatalogSeed.SkillId(1), level = "Advanced", description = (string?)null } },
                learn = new[] { new { skillId = SkillCatalogSeed.SkillId(24), level = "Beginner", description = (string?)null } },
            });
            put.EnsureSuccessStatusCode();
        }

        return new Person(token, me.GetProperty("id").GetGuid(), email);
    }

    private async Task<HttpResponseMessage> SendAsync(string? token, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _api.Client.SendAsync(request);
    }

    private static object Profile(
        string name = "Алиса", string? bio = null, string uiLanguage = "ru", string timeZone = "Asia/Dushanbe",
        string[]? languages = null, bool isSearchable = false) =>
        new { name, bio, uiLanguage, timeZone, languages = languages ?? ["ru"], isSearchable };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private Task ModifyUserAsync(Guid id, Action<User> change) => _factory.QueryAsync(async db =>
    {
        var user = await db.Users.SingleAsync(u => u.Id == id);
        change(user);
        await db.SaveChangesAsync();
        return 0;
    });

    private static void Set<T>(User user, string property, T value) =>
        typeof(User).GetProperty(property)!.SetValue(user, value);

    // ---------- доступ ----------

    [Fact]
    public async Task Every_profile_endpoint_requires_a_login()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Put, "/me", Profile())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, $"/users/{Guid.NewGuid()}")).StatusCode);
    }

    // ---------- мой профиль ----------

    [Fact]
    public async Task A_new_profile_has_safe_defaults_and_shows_the_owners_private_data()
    {
        var person = await NewPersonAsync(anketa: false);

        var me = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me"));

        Assert.Equal(person.Id, me.GetProperty("id").GetGuid());
        Assert.Equal(person.Email, me.GetProperty("email").GetString());
        Assert.Equal("User", me.GetProperty("role").GetString());
        Assert.Equal("Regular", me.GetProperty("accountType").GetString());
        Assert.Equal("Test User", me.GetProperty("name").GetString());
        Assert.Equal("2000-01-01", me.GetProperty("dateOfBirth").GetString());
        Assert.False(me.GetProperty("isSearchable").GetBoolean());       // скрыт, пока сам не включит
        Assert.False(me.GetProperty("anketaComplete").GetBoolean());
        Assert.Equal(0, me.GetProperty("languages").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("ratingAvg").ValueKind);
        Assert.True(me.GetProperty("isNewcomer").GetBoolean());
        Assert.False(me.GetProperty("twoFactorEnabled").GetBoolean());
    }

    [Fact]
    public async Task A_profile_can_be_replaced_and_the_change_is_visible_on_the_next_read()
    {
        var person = await NewPersonAsync();

        var put = await SendAsync(person.Token, HttpMethod.Put, "/me",
            Profile(name: "  Алиса Каримова ", bio: "  Учу Python.  ", uiLanguage: "tg", timeZone: "Europe/Moscow", languages: ["ru", "TG", " ru "], isSearchable: true));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await JsonAsync(put);
        Assert.Equal("Алиса Каримова", updated.GetProperty("name").GetString());   // пробелы по краям убраны
        Assert.Equal("Учу Python.", updated.GetProperty("bio").GetString());
        Assert.Equal(["ru", "tg"], updated.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));   // без повторов, по алфавиту

        var me = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me"));
        Assert.Equal("tg", me.GetProperty("uiLanguage").GetString());
        Assert.Equal("Europe/Moscow", me.GetProperty("timeZone").GetString());
        Assert.True(me.GetProperty("isSearchable").GetBoolean());
        Assert.Equal(["ru", "tg"], me.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public async Task Languages_are_replaced_not_accumulated_and_can_be_cleared()
    {
        var person = await NewPersonAsync();

        await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(languages: ["ru", "en"]));
        var second = await JsonAsync(await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(languages: ["en", "tg"])));
        Assert.Equal(["en", "tg"], second.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));

        var cleared = await JsonAsync(await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(languages: [])));
        Assert.Equal(0, cleared.GetProperty("languages").GetArrayLength());
    }

    [Fact]
    public async Task An_empty_bio_clears_it()
    {
        var person = await NewPersonAsync();
        await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(bio: "что-то"));

        var cleared = await JsonAsync(await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(bio: "   ")));

        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("bio").ValueKind);
    }

    [Fact]
    public async Task Protected_fields_cannot_be_changed_through_the_profile()
    {
        var person = await NewPersonAsync();

        var put = await SendAsync(person.Token, HttpMethod.Put, "/me", new
        {
            name = "Хакер", uiLanguage = "en", timeZone = "UTC", languages = Array.Empty<string>(), isSearchable = false,
            email = "evil@example.com", dateOfBirth = "1980-01-01", role = "SuperAdmin", avatarUrl = "https://evil.example/x.png",
            ratingAvg = 5.0, ratingCount = 99, status = "Active",
        });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var me = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me"));
        Assert.Equal(person.Email, me.GetProperty("email").GetString());
        Assert.Equal("2000-01-01", me.GetProperty("dateOfBirth").GetString());
        Assert.Equal("User", me.GetProperty("role").GetString());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("avatarUrl").ValueKind);
        Assert.Equal(0, me.GetProperty("ratingCount").GetInt32());
    }

    // ---------- проверка данных ----------

    [Theory]
    [InlineData("name", "")]
    [InlineData("name", "   ")]
    [InlineData("uiLanguage", "de")]
    [InlineData("uiLanguage", "")]
    [InlineData("timeZone", "Mars/Phobos")]
    [InlineData("timeZone", "Tajikistan Standard Time")]
    [InlineData("timeZone", "asia/dushanbe")]
    [InlineData("timeZone", "Asia/DUSHANBE")]
    [InlineData("timeZone", "../etc/passwd")]
    [InlineData("timeZone", "")]
    public async Task Invalid_text_fields_are_a_400_with_field_errors(string field, string value)
    {
        var person = await NewPersonAsync();
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Алиса", ["bio"] = null, ["uiLanguage"] = "ru", ["timeZone"] = "Asia/Dushanbe",
            ["languages"] = new[] { "ru" }, ["isSearchable"] = false,
        };
        body[field] = value;

        var response = await SendAsync(person.Token, HttpMethod.Put, "/me", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task Name_has_a_length_limit_of_100()
    {
        var person = await NewPersonAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(name: new string('Ю', 100)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(name: new string('Ю', 101)))).StatusCode);
    }

    [Fact]
    public async Task Bio_has_a_length_limit_of_1000()
    {
        var person = await NewPersonAsync();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(bio: new string('я', 1000)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(bio: new string('я', 1001)))).StatusCode);
    }

    [Theory]
    [InlineData("xx")]          // нет такого кода
    [InlineData("zz")]
    [InlineData("eng")]         // ISO 639-2, не 639-1
    [InlineData("r")]
    [InlineData("")]
    [InlineData("русский")]
    [InlineData("r u")]
    [InlineData("bh")]          // составной код, не язык
    public async Task A_lesson_language_must_be_an_iso_639_1_code_from_the_list(string code)
    {
        var person = await NewPersonAsync();

        var response = await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(languages: ["ru", code]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task Lesson_languages_are_not_limited_to_the_interface_languages()
    {
        var person = await NewPersonAsync();

        var put = await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(uiLanguage: "ru", languages: ["fr", " DE ", "zh", "ar", "fa", "uz"]));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(["ar", "de", "fa", "fr", "uz", "zh"],
            (await JsonAsync(put)).GetProperty("languages").EnumerateArray().Select(l => l.GetString()));   // верхний регистр и пробелы приведены к виду из списка
    }

    [Fact]
    public async Task The_interface_language_is_still_only_tg_ru_or_en()
    {
        var person = await NewPersonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(uiLanguage: "fr"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(uiLanguage: "en"))).StatusCode);
    }

    [Fact]
    public async Task At_most_ten_lesson_languages_can_be_listed()
    {
        var person = await NewPersonAsync();
        var ten = new[] { "tg", "ru", "en", "de", "fr", "es", "it", "zh", "ar", "fa" };

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(languages: ten))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(languages: [.. ten, "ja"]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(languages: [.. ten, "ru", "RU"]))).StatusCode);   // повторы не считаются
    }

    [Fact]
    public async Task Missing_required_fields_are_refused()
    {
        var person = await NewPersonAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(person.Token, HttpMethod.Put, "/me",
            new { name = "A", uiLanguage = "ru", timeZone = "UTC", isSearchable = false })).StatusCode);          // нет languages
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(person.Token, HttpMethod.Put, "/me",
            new { name = "A", uiLanguage = "ru", timeZone = "UTC", languages = new[] { "ru" } })).StatusCode);    // нет isSearchable
    }

    [Theory]
    [InlineData("Asia/Dushanbe")]
    [InlineData("Europe/Moscow")]
    [InlineData("UTC")]
    [InlineData("Etc/GMT+5")]
    [InlineData("America/Argentina/Buenos_Aires")]
    public async Task Real_iana_time_zones_are_accepted(string zone)
    {
        var person = await NewPersonAsync();

        var response = await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(timeZone: zone));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(zone, (await JsonAsync(response)).GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task A_rejected_update_changes_nothing()
    {
        var person = await NewPersonAsync();
        await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(name: "Было", languages: ["ru"]));

        await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(name: "Стало", timeZone: "Mars/Phobos", languages: ["en"]));

        var me = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me"));
        Assert.Equal("Было", me.GetProperty("name").GetString());
        Assert.Equal(["ru"], me.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public async Task Parallel_updates_never_cause_a_server_error_and_leave_a_consistent_profile()
    {
        var person = await NewPersonAsync();
        var sets = new[] { new[] { "ru" }, new[] { "en" }, new[] { "tg" }, new[] { "ru", "en" }, new[] { "en", "tg" }, new[] { "ru", "tg" } };

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            SendAsync(person.Token, HttpMethod.Put, "/me", Profile(name: "Имя" + i, languages: sets[i % sets.Length]))));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"got {r.StatusCode}"));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);

        var me = await JsonAsync(await SendAsync(person.Token, HttpMethod.Get, "/me"));
        var languages = me.GetProperty("languages").EnumerateArray().Select(l => l.GetString()!).ToArray();
        Assert.Contains(sets, s => s.Order().SequenceEqual(languages));   // итог — один из присланных наборов целиком
    }

    // ---------- служебные аккаунты ----------

    private async Task<User> StaffInDbAsync(UserRole role)
    {
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", role);
        await _factory.QueryAsync(async db =>
        {
            db.Users.Add(staff);
            await db.SaveChangesAsync();
            return 0;
        });
        return staff;
    }

    [Fact]
    public async Task A_staff_account_has_no_profile_it_can_read_itself_but_not_edit_or_browse_others()
    {
        var staff = await StaffInDbAsync(UserRole.Admin);
        var token = TestTokens.ForUser(_factory, staff);
        var person = await NewPersonAsync();
        await SendAsync(person.Token, HttpMethod.Put, "/me", Profile(isSearchable: true));

        var me = await SendAsync(token, HttpMethod.Get, "/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal("Staff", (await JsonAsync(me)).GetProperty("accountType").GetString());

        var put = await SendAsync(token, HttpMethod.Put, "/me", Profile());
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal("staff_has_no_profile", await AuthApi.ReadCodeAsync(put));

        var browse = await SendAsync(token, HttpMethod.Get, $"/users/{person.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, browse.StatusCode);
        Assert.Equal("regular_account_required", await AuthApi.ReadCodeAsync(browse));
    }

    [Fact]
    public async Task A_staff_profile_cannot_be_viewed_by_users_even_if_marked_searchable()
    {
        var staff = await StaffInDbAsync(UserRole.Moderator);
        await ModifyUserAsync(staff.Id, u => u.IsSearchable = true);
        var person = await NewPersonAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(person.Token, HttpMethod.Get, $"/users/{staff.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_blocked_account_cannot_edit_its_profile()
    {
        var person = await NewPersonAsync();
        await ModifyUserAsync(person.Id, u => Set(u, nameof(User.Status), UserStatus.Blocked));

        var put = await SendAsync(person.Token, HttpMethod.Put, "/me", Profile());

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal("account_not_active", await AuthApi.ReadCodeAsync(put));
    }

    // ---------- чужой профиль ----------

    [Fact]
    public async Task A_hidden_profile_is_a_404_for_others_but_visible_to_its_owner()
    {
        var owner = await NewPersonAsync();
        var other = await NewPersonAsync();

        var hidden = await SendAsync(other.Token, HttpMethod.Get, $"/users/{owner.Id}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("user_not_found", await AuthApi.ReadCodeAsync(hidden));

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(owner.Token, HttpMethod.Get, $"/users/{owner.Id}")).StatusCode);

        await SendAsync(owner.Token, HttpMethod.Put, "/me", Profile(isSearchable: true));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(other.Token, HttpMethod.Get, $"/users/{owner.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_public_profile_shows_only_public_data()
    {
        var owner = await NewPersonAsync();
        var other = await NewPersonAsync();
        await SendAsync(owner.Token, HttpMethod.Put, "/me", Profile(name: "Рустам", bio: "Дизайнер", timeZone: "Asia/Dushanbe", languages: ["tg", "ru"], isSearchable: true));

        var profile = await JsonAsync(await SendAsync(other.Token, HttpMethod.Get, $"/users/{owner.Id}"));

        Assert.Equal("Рустам", profile.GetProperty("name").GetString());
        Assert.Equal("Дизайнер", profile.GetProperty("bio").GetString());
        Assert.Equal("Asia/Dushanbe", profile.GetProperty("timeZone").GetString());
        Assert.Equal(["ru", "tg"], profile.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
        Assert.False(profile.GetProperty("isMinor").GetBoolean());
        Assert.True(profile.GetProperty("isNewcomer").GetBoolean());

        var keys = profile.EnumerateObject().Select(p => p.Name).ToHashSet();
        foreach (var secret in new[] { "email", "dateOfBirth", "role", "twoFactorEnabled", "isSearchable", "uiLanguage", "lastActiveAt", "status" })
        {
            Assert.DoesNotContain(secret, keys);
        }
    }

    [Theory]
    [InlineData(UserStatus.Blocked)]
    [InlineData(UserStatus.Deleted)]
    public async Task Blocked_and_deleted_users_look_exactly_like_missing_ones(UserStatus status)
    {
        var owner = await NewPersonAsync();
        var other = await NewPersonAsync();
        await SendAsync(owner.Token, HttpMethod.Put, "/me", Profile(isSearchable: true));
        await ModifyUserAsync(owner.Id, u => Set(u, nameof(User.Status), status));

        var gone = await SendAsync(other.Token, HttpMethod.Get, $"/users/{owner.Id}");
        var missing = await SendAsync(other.Token, HttpMethod.Get, $"/users/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        // Тело тоже одинаково (кроме traceId, он у каждого запроса свой).
        static string WithoutTrace(string json) => System.Text.RegularExpressions.Regex.Replace(json, "\"traceId\":\"[^\"]*\"", "");
        Assert.Equal(WithoutTrace(await missing.Content.ReadAsStringAsync()), WithoutTrace(await gone.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task A_malformed_id_is_not_a_server_error()
    {
        var person = await NewPersonAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(person.Token, HttpMethod.Get, "/users/not-a-guid")).StatusCode);
    }

    // ---------- рейтинг и возраст ----------

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(40, true)]
    public async Task Fewer_than_three_reviews_means_newcomer_without_an_average(int count, bool average)
    {
        var owner = await NewPersonAsync();
        var other = await NewPersonAsync();
        await SendAsync(owner.Token, HttpMethod.Put, "/me", Profile(isSearchable: true));
        await ModifyUserAsync(owner.Id, u =>
        {
            Set(u, nameof(User.RatingCount), count);
            Set(u, nameof(User.RatingAvg), 4.5m);
        });

        var profile = await JsonAsync(await SendAsync(other.Token, HttpMethod.Get, $"/users/{owner.Id}"));
        var mine = await JsonAsync(await SendAsync(owner.Token, HttpMethod.Get, "/me"));

        foreach (var view in new[] { profile, mine })
        {
            Assert.Equal(count, view.GetProperty("ratingCount").GetInt32());
            Assert.Equal(!average, view.GetProperty("isNewcomer").GetBoolean());
            if (average)
            {
                Assert.Equal(4.5m, view.GetProperty("ratingAvg").GetDecimal());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, view.GetProperty("ratingAvg").ValueKind);
            }
        }
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, true)]
    [InlineData(18, false)]
    [InlineData(30, false)]
    public async Task Teenagers_are_marked_without_revealing_the_birth_date(int age, bool expectedMinor)
    {
        var owner = await NewPersonAsync();
        var other = await NewPersonAsync();
        await SendAsync(owner.Token, HttpMethod.Put, "/me", Profile(isSearchable: true));
        var today = DateOnly.FromDateTime(_factory.Clock.GetUtcNow().UtcDateTime);
        await ModifyUserAsync(owner.Id, u => Set(u, nameof(User.DateOfBirth), (DateOnly?)today.AddYears(-age).AddDays(-1)));

        var profile = await JsonAsync(await SendAsync(other.Token, HttpMethod.Get, $"/users/{owner.Id}"));

        Assert.Equal(expectedMinor, profile.GetProperty("isMinor").GetBoolean());
        Assert.False(profile.TryGetProperty("dateOfBirth", out _));
    }
}
