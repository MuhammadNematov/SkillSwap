using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Settings;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Staff;

/// <summary>Системные настройки суперадмина: чтение, изменение с подтверждением 2FA, проверка значений, аудит прежнего и нового значения, действие сразу.</summary>
[Collection(ApiCollection.Name)]
public class SystemSettingsApiTests : IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public SystemSettingsApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Настройки общие для всей базы тестов: после каждого теста возвращаем значения по умолчанию, чтобы не влиять на другие.</summary>
    public async Task DisposeAsync() => await _factory.QueryAsync(async db =>
    {
        foreach (var d in SystemSettingDefinitions.All)
        {
            await db.SystemSettings.Where(s => s.Key == d.Key).ExecuteUpdateAsync(x => x.SetProperty(s => s.Value, d.Default));
        }

        return 0;
    });

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

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    private Task<StaffActor> SuperAsync() => StaffActor.CreateAsync(_factory, UserRole.SuperAdmin);

    private Task<HttpResponseMessage> PutAsync(StaffActor actor, object[] settings, string? reason = null, object? confirmation = null) =>
        SendAsync(actor.Token, HttpMethod.Put, "/superadmin/settings", new { settings, reason, confirmation = confirmation ?? actor.Confirm() });

    private static object Change(string key, string value) => new { key, value };

    private Task<string?> StoredAsync(string key) => _factory.QueryAsync(db => db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync());

    private Task<List<AuditLog>> AuditAsync(Guid actor) =>
        _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action == "SettingChanged" && a.ActorId == actor).OrderBy(a => a.CreatedAt).ToListAsync());

    // ======================= доступ и чтение =======================

    [Fact]
    public async Task Only_a_live_superadmin_opens_the_settings()
    {
        var superAdmin = await SuperAsync();
        var admin = await StaffActor.CreateAsync(_factory, UserRole.Admin);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var (_, personal) = await StaffActor.PersonalAsync(_factory);
        var removed = await SuperAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == removed.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Status, UserStatus.Blocked));
            return 0;
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/superadmin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(personal, HttpMethod.Get, "/superadmin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Get, "/superadmin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(admin.Token, HttpMethod.Get, "/superadmin/settings")).StatusCode);   // админу нельзя, даже с подтверждением
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(admin, [Change("Requests.MaxPerDay", "11")])).StatusCode);
        Assert.Equal("staff_not_active", await CodeAsync(await SendAsync(removed.Token, HttpMethod.Get, "/superadmin/settings")));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(superAdmin.Token, HttpMethod.Get, "/superadmin/settings")).StatusCode);
        Assert.Equal("10", await StoredAsync("Requests.MaxPerDay"));   // отказанные запросы ничего не меняют
    }

    [Fact]
    public async Task The_list_shows_every_setting_with_its_type_bounds_and_default()
    {
        var superAdmin = await SuperAsync();

        var list = (await JsonAsync(await SendAsync(superAdmin.Token, HttpMethod.Get, "/superadmin/settings"))).EnumerateArray().ToList();

        Assert.Equal(SystemSettingDefinitions.All.Select(d => d.Key), list.Select(i => i.GetProperty("key").GetString()));
        var perDay = list.Single(i => i.GetProperty("key").GetString() == "Requests.MaxPerDay");
        Assert.Equal("10", perDay.GetProperty("value").GetString());
        Assert.Equal("Int", perDay.GetProperty("kind").GetString());
        Assert.Equal(1, perDay.GetProperty("min").GetInt32());
        Assert.Equal(200, perDay.GetProperty("max").GetInt32());
        Assert.Equal("10", perDay.GetProperty("default").GetString());
        Assert.True(perDay.GetProperty("isDefault").GetBoolean());
        var minors = list.Single(i => i.GetProperty("key").GetString() == "Matching.MinorsMatchOnlyMinors");
        Assert.Equal("Bool", minors.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, minors.GetProperty("min").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(minors.GetProperty("description").GetString()));
    }

    // ======================= изменение =======================

    [Fact]
    public async Task A_change_is_applied_audited_with_the_old_and_new_value_and_takes_effect_at_once()
    {
        var superAdmin = await SuperAsync();

        var response = await PutAsync(superAdmin, [Change("Requests.MaxPerDay", "25"), Change("Matching.MinorsMatchOnlyMinors", "FALSE")], reason: "  Запуск акции: временно больше заявок.  ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await JsonAsync(response);
        Assert.Equal(["Requests.MaxPerDay", "Matching.MinorsMatchOnlyMinors"], result.GetProperty("changed").EnumerateArray().Select(x => x.GetString()));
        var perDay = result.GetProperty("settings").EnumerateArray().Single(i => i.GetProperty("key").GetString() == "Requests.MaxPerDay");
        Assert.Equal("25", perDay.GetProperty("value").GetString());
        Assert.False(perDay.GetProperty("isDefault").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, perDay.GetProperty("updatedAt").ValueKind);
        Assert.Equal("25", await StoredAsync("Requests.MaxPerDay"));
        Assert.Equal("false", await StoredAsync("Matching.MinorsMatchOnlyMinors"));   // приведено к хранимому виду

        var audit = await AuditAsync(superAdmin.Id);
        Assert.Equal(2, audit.Count);
        var details = audit.Select(a => JsonDocument.Parse(a.DetailsJson).RootElement).ToDictionary(d => d.GetProperty("key").GetString()!);
        Assert.Equal("10", details["Requests.MaxPerDay"].GetProperty("oldValue").GetString());
        Assert.Equal("25", details["Requests.MaxPerDay"].GetProperty("newValue").GetString());
        Assert.Equal("true", details["Matching.MinorsMatchOnlyMinors"].GetProperty("oldValue").GetString());
        Assert.Equal("false", details["Matching.MinorsMatchOnlyMinors"].GetProperty("newValue").GetString());
        Assert.Equal("Запуск акции: временно больше заявок.", details["Requests.MaxPerDay"].GetProperty("reason").GetString());
        Assert.All(audit, a => Assert.Equal("SystemSetting", a.TargetType));

        // Код читает настройки из базы при каждом использовании: новое значение действует сразу.
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ISystemSettingsStore>();
        Assert.Equal(25, await store.GetIntAsync("Requests.MaxPerDay", 10, CancellationToken.None));
        Assert.False(await store.GetBoolAsync("Matching.MinorsMatchOnlyMinors", true, CancellationToken.None));
    }

    [Fact]
    public async Task The_same_value_is_not_a_change_and_is_not_audited()
    {
        var superAdmin = await SuperAsync();

        var response = await PutAsync(superAdmin, [Change("Requests.MaxActiveExchanges", "3"), Change("Requests.DeclineCooldownDays", "0")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Requests.DeclineCooldownDays"], (await JsonAsync(response)).GetProperty("changed").EnumerateArray().Select(x => x.GetString()));
        Assert.Single(await AuditAsync(superAdmin.Id));
        Assert.Equal("0", await StoredAsync("Requests.DeclineCooldownDays"));   // ноль дней допустим: повторная заявка сразу
    }

    [Fact]
    public async Task A_missing_row_is_created_with_the_value_and_the_default_description()
    {
        var superAdmin = await SuperAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.SystemSettings.Where(s => s.Key == "Requests.MaxPendingOutgoing").ExecuteDeleteAsync();
            return 0;
        });

        var response = await PutAsync(superAdmin, [Change("Requests.MaxPendingOutgoing", "7")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("7", await StoredAsync("Requests.MaxPendingOutgoing"));
        var details = JsonDocument.Parse((await AuditAsync(superAdmin.Id)).Single().DetailsJson).RootElement;
        Assert.Equal("5", details.GetProperty("oldValue").GetString());   // прежним считается значение по умолчанию
    }

    // ======================= проверки =======================

    [Theory]
    [InlineData("Requests.MaxPerDay", "0")]               // меньше границы
    [InlineData("Requests.MaxPerDay", "201")]             // больше границы
    [InlineData("Requests.MaxPerDay", "-5")]
    [InlineData("Requests.MaxPerDay", "ten")]
    [InlineData("Requests.MaxPerDay", "1.5")]
    [InlineData("Requests.MaxPerDay", "")]
    [InlineData("Requests.MaxPerDay", "99999999999")]
    [InlineData("Requests.DeclineCooldownDays", "366")]
    [InlineData("Requests.MaxActiveExchanges", "21")]
    [InlineData("Matching.MinorsMatchOnlyMinors", "yes")]
    [InlineData("Matching.MinorsMatchOnlyMinors", "1")]
    public async Task A_value_outside_the_type_or_bounds_is_refused_and_nothing_changes(string key, string value)
    {
        var superAdmin = await SuperAsync();
        var before = await StoredAsync(key);

        var code = new { password = AuthApi.Password, code = superAdmin.Recovery[0], isRecoveryCode = true };

        var response = await PutAsync(superAdmin, [Change(key, value)], confirmation: code);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await JsonAsync(response);
        Assert.Equal("invalid_setting_value", problem.GetProperty("code").GetString());
        Assert.Equal(key, problem.GetProperty("key").GetString());
        Assert.Equal(before, await StoredAsync(key));
        Assert.Empty(await AuditAsync(superAdmin.Id));
        // Подтверждение проверяется после значений: отказ по значению не сжигает одноразовый код.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(superAdmin, [Change("Requests.MaxPerDay", "11")], confirmation: code)).StatusCode);
    }

    [Fact]
    public async Task One_bad_value_cancels_the_whole_batch_and_unknown_or_repeated_keys_are_refused()
    {
        var superAdmin = await SuperAsync();

        var mixed = await PutAsync(superAdmin, [Change("Requests.MaxPerDay", "30"), Change("Requests.MaxPendingOutgoing", "0")]);
        var unknown = await PutAsync(superAdmin, [Change("Requests.NoSuchThing", "5")]);
        var repeated = await PutAsync(superAdmin, [Change("Requests.MaxPerDay", "30"), Change("Requests.MaxPerDay", "31")]);
        var none = await PutAsync(superAdmin, []);
        var noBody = await SendAsync(superAdmin.Token, HttpMethod.Put, "/superadmin/settings", new { confirmation = superAdmin.Confirm() });
        var longReason = await PutAsync(superAdmin, [Change("Requests.MaxPerDay", "30")], reason: new string('я', 501));

        Assert.Equal("invalid_setting_value", await CodeAsync(mixed));
        Assert.Equal("unknown_setting", await CodeAsync(unknown));
        Assert.Equal("duplicate_setting", await CodeAsync(repeated));
        Assert.Equal("no_settings", await CodeAsync(none));
        Assert.Equal("no_settings", await CodeAsync(noBody));
        Assert.Equal("reason_too_long", await CodeAsync(longReason));
        Assert.Equal("10", await StoredAsync("Requests.MaxPerDay"));   // ни одна часть пакета не применилась
        Assert.Empty(await AuditAsync(superAdmin.Id));
    }

    [Fact]
    public async Task Every_change_needs_the_password_and_a_fresh_second_factor()
    {
        var superAdmin = await SuperAsync();

        var none = await SendAsync(superAdmin.Token, HttpMethod.Put, "/superadmin/settings", new { settings = new[] { Change("Requests.MaxPerDay", "30") } });
        var wrong = await PutAsync(superAdmin, [Change("Requests.MaxPerDay", "30")], confirmation: new { password = "Wrong-password-1", code = superAdmin.Recovery[superAdmin.Next++], isRecoveryCode = true });
        var noCode = await PutAsync(superAdmin, [Change("Requests.MaxPerDay", "30")], confirmation: new { password = AuthApi.Password });
        var spent = superAdmin.Recovery[superAdmin.Next++];
        var first = await PutAsync(superAdmin, [Change("Requests.MaxPerDay", "30")], confirmation: new { password = AuthApi.Password, code = spent, isRecoveryCode = true });
        var again = await PutAsync(superAdmin, [Change("Requests.MaxPerDay", "40")], confirmation: new { password = AuthApi.Password, code = spent, isRecoveryCode = true });

        Assert.Equal("step_up_required", await CodeAsync(none));
        Assert.Equal("invalid_password", await CodeAsync(wrong));
        Assert.Equal("two_factor_code_required", await CodeAsync(noCode));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);   // одноразовый код второй раз не подходит
        Assert.Equal("invalid_two_factor_code", await CodeAsync(again));
        Assert.Equal("30", await StoredAsync("Requests.MaxPerDay"));
    }
}
