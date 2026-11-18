using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence.Seed;

namespace SkillSwap.Api.IntegrationTests.Profile;

/// <summary>Блокировка пользователей (раздел 12 ТЗ): POST/DELETE /users/{id}/block, GET /me/blocks и учёт блокировки в профиле.</summary>
[Collection(ApiCollection.Name)]
public class UserBlockTests
{
    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public UserBlockTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    private sealed record Person(string Token, Guid Id);

    /// <summary>Пользователь с полной анкетой: после неё он виден другим автоматически.</summary>
    private async Task<Person> NewPersonAsync()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var token = (await _api.LoginSessionAsync(email)).AccessToken;
        var me = await JsonAsync(await SendAsync(token, HttpMethod.Get, "/me"));
        (await SendAsync(token, HttpMethod.Put, "/me/skills", new
        {
            teach = new[] { new { skillId = SkillCatalogSeed.SkillId(1), level = "Beginner", description = (string?)null } },
            learn = new[] { new { skillId = SkillCatalogSeed.SkillId(24), level = "Beginner", description = (string?)null } },
        })).EnsureSuccessStatusCode();
        return new Person(token, me.GetProperty("id").GetGuid());
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

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private Task<HttpResponseMessage> BlockAsync(Person by, Guid target) => SendAsync(by.Token, HttpMethod.Post, $"/users/{target}/block");
    private Task<HttpResponseMessage> UnblockAsync(Person by, Guid target) => SendAsync(by.Token, HttpMethod.Delete, $"/users/{target}/block");
    private Task<HttpResponseMessage> ProfileAsync(Person viewer, Guid target) => SendAsync(viewer.Token, HttpMethod.Get, $"/users/{target}");

    private async Task<bool> CanSeeAsync(Person viewer, Person target) =>
        (await ProfileAsync(viewer, target.Id)).StatusCode == HttpStatusCode.OK;

    private Task<int> RowsAsync(Guid blocker) => _factory.QueryAsync(db => db.UserBlocks.CountAsync(b => b.UserId == blocker));

    /// <summary>Создаёт в БД n обычных пользователей напрямую (дёшево) и возвращает их id.</summary>
    private Task<List<Guid>> SeedUsersAsync(int n) => _factory.QueryAsync(async db =>
    {
        var users = Enumerable.Range(0, n).Select(_ => User.CreateRegular(AuthApi.NewEmail(), "Seed", new DateOnly(1990, 1, 1), "en")).ToList();
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
        return users.Select(u => u.Id).ToList();
    });

    // ---------- доступ ----------

    [Fact]
    public async Task Every_endpoint_requires_a_login()
    {
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Post, $"/users/{id}/block")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Delete, $"/users/{id}/block")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/me/blocks")).StatusCode);
    }

    // ---------- блокировка и её последствия ----------

    [Fact]
    public async Task Blocking_hides_the_two_users_from_each_other_but_not_from_everyone_else()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();
        var carol = await NewPersonAsync();
        Assert.True(await CanSeeAsync(alice, bob));
        Assert.True(await CanSeeAsync(bob, alice));

        var block = await BlockAsync(alice, bob.Id);

        Assert.Equal(HttpStatusCode.NoContent, block.StatusCode);
        Assert.False(await CanSeeAsync(alice, bob));    // заблокировавший тоже не видит
        Assert.False(await CanSeeAsync(bob, alice));    // и заблокированный не видит заблокировавшего
        Assert.True(await CanSeeAsync(carol, alice));   // третьи лица не затронуты
        Assert.True(await CanSeeAsync(carol, bob));
        Assert.True(await CanSeeAsync(alice, carol));
    }

    [Fact]
    public async Task The_blocked_user_cannot_tell_a_block_from_a_missing_profile()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();
        await BlockAsync(alice, bob.Id);

        var blocked = await ProfileAsync(bob, alice.Id);
        var missing = await ProfileAsync(bob, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);
        string WithoutTrace(string json) => Regex.Replace(json, "\"traceId\":\"[^\"]*\"", "");
        Assert.Equal(WithoutTrace(await missing.Content.ReadAsStringAsync()), WithoutTrace(await blocked.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Blocking_does_not_touch_the_blocked_users_own_account()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();

        await BlockAsync(alice, bob.Id);

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(bob.Token, HttpMethod.Get, "/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(bob.Token, HttpMethod.Get, "/me/skills")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ProfileAsync(bob, bob.Id)).StatusCode);
        Assert.Empty((await JsonAsync(await SendAsync(bob.Token, HttpMethod.Get, "/me/blocks"))).EnumerateArray());   // у него в списке никого
    }

    [Fact]
    public async Task Unblocking_restores_visibility_and_works_when_nothing_was_blocked()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();
        await BlockAsync(alice, bob.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await UnblockAsync(alice, bob.Id)).StatusCode);
        Assert.True(await CanSeeAsync(alice, bob));
        Assert.True(await CanSeeAsync(bob, alice));

        Assert.Equal(HttpStatusCode.NoContent, (await UnblockAsync(alice, bob.Id)).StatusCode);          // повторно: не ошибка
        Assert.Equal(HttpStatusCode.NoContent, (await UnblockAsync(alice, Guid.NewGuid())).StatusCode);  // и для чужого id
    }

    [Fact]
    public async Task Blocking_twice_is_not_an_error_and_leaves_one_row()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await BlockAsync(alice, bob.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await BlockAsync(alice, bob.Id)).StatusCode);

        Assert.Equal(1, await RowsAsync(alice.Id));
    }

    [Fact]
    public async Task A_mutual_block_needs_both_sides_to_be_lifted()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();
        await BlockAsync(alice, bob.Id);
        await BlockAsync(bob, alice.Id);

        await UnblockAsync(alice, bob.Id);
        Assert.False(await CanSeeAsync(alice, bob));   // bob всё ещё блокирует alice
        Assert.False(await CanSeeAsync(bob, alice));

        await UnblockAsync(bob, alice.Id);
        Assert.True(await CanSeeAsync(alice, bob));
        Assert.True(await CanSeeAsync(bob, alice));
    }

    [Fact]
    public async Task A_block_is_one_way_data_unblocking_by_the_other_side_does_nothing()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();
        await BlockAsync(alice, bob.Id);

        await UnblockAsync(bob, alice.Id);   // bob не блокировал, снимать нечего

        Assert.False(await CanSeeAsync(bob, alice));
        Assert.Equal(1, await RowsAsync(alice.Id));
    }

    // ---------- список ----------

    [Fact]
    public async Task The_list_shows_who_I_blocked_newest_first_and_lets_me_unblock_by_id()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();
        var carol = await NewPersonAsync();
        await BlockAsync(alice, bob.Id);
        await BlockAsync(alice, carol.Id);
        // Время блокировки ставится часами приложения; общие часы набора сдвигать нельзя (на них опираются другие тесты),
        // поэтому «старую» блокировку делаем старой прямо в БД.
        await _factory.QueryAsync(async db =>
        {
            var older = await db.UserBlocks.SingleAsync(b => b.UserId == alice.Id && b.BlockedUserId == bob.Id);
            older.CreatedAt = older.CreatedAt.AddMinutes(-1);
            await db.SaveChangesAsync();
            return 0;
        });

        var list = (await JsonAsync(await SendAsync(alice.Token, HttpMethod.Get, "/me/blocks"))).EnumerateArray().ToList();

        Assert.Equal([carol.Id, bob.Id], list.Select(b => b.GetProperty("userId").GetGuid()));
        Assert.Equal("Test User", list[0].GetProperty("name").GetString());
        Assert.NotEqual(default, list[0].GetProperty("blockedAt").GetDateTime());
        Assert.False(list[0].TryGetProperty("email", out _));
        Assert.True(list[0].TryGetProperty("avatarUrl", out _));

        await UnblockAsync(alice, list[0].GetProperty("userId").GetGuid());
        Assert.Single((await JsonAsync(await SendAsync(alice.Token, HttpMethod.Get, "/me/blocks"))).EnumerateArray());
    }

    [Fact]
    public async Task Deleted_accounts_disappear_from_the_list()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();
        await BlockAsync(alice, bob.Id);
        await _factory.QueryAsync(async db =>
        {
            typeof(User).GetProperty(nameof(User.Status))!.SetValue(await db.Users.SingleAsync(u => u.Id == bob.Id), UserStatus.Deleted);
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Empty((await JsonAsync(await SendAsync(alice.Token, HttpMethod.Get, "/me/blocks"))).EnumerateArray());
    }

    // ---------- нельзя ----------

    [Fact]
    public async Task You_cannot_block_yourself()
    {
        var alice = await NewPersonAsync();

        var response = await BlockAsync(alice, alice.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("cannot_block_self", await AuthApi.ReadCodeAsync(response));
        Assert.Equal(0, await RowsAsync(alice.Id));
    }

    [Fact]
    public async Task An_unknown_staff_or_deleted_target_is_a_404()
    {
        var alice = await NewPersonAsync();
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Moderator);
        var gone = await SeedUsersAsync(1);
        await _factory.QueryAsync(async db =>
        {
            db.Users.Add(staff);
            typeof(User).GetProperty(nameof(User.Status))!.SetValue(await db.Users.SingleAsync(u => u.Id == gone[0]), UserStatus.Deleted);
            await db.SaveChangesAsync();
            return 0;
        });

        foreach (var id in new[] { Guid.NewGuid(), staff.Id, gone[0] })
        {
            var response = await BlockAsync(alice, id);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("user_not_found", await AuthApi.ReadCodeAsync(response));
        }

        Assert.Equal(0, await RowsAsync(alice.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(alice.Token, HttpMethod.Post, "/users/not-a-guid/block")).StatusCode);
    }

    [Fact]
    public async Task A_hidden_or_admin_blocked_user_can_still_be_blocked()
    {
        var alice = await NewPersonAsync();
        var hidden = await NewPersonAsync();
        await SendAsync(hidden.Token, HttpMethod.Put, "/me", new
        {
            name = "Х", bio = (string?)null, uiLanguage = "en", timeZone = "UTC", languages = new[] { "en" }, isSearchable = false,
        });
        var banned = await NewPersonAsync();
        await _factory.QueryAsync(async db =>
        {
            typeof(User).GetProperty(nameof(User.Status))!.SetValue(await db.Users.SingleAsync(u => u.Id == banned.Id), UserStatus.Blocked);
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(HttpStatusCode.NoContent, (await BlockAsync(alice, hidden.Id)).StatusCode);   // партнёр по обмену может быть скрыт
        Assert.Equal(HttpStatusCode.NoContent, (await BlockAsync(alice, banned.Id)).StatusCode);
    }

    [Fact]
    public async Task Staff_accounts_have_no_blocks()
    {
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Admin);
        await _factory.QueryAsync(async db =>
        {
            db.Users.Add(staff);
            await db.SaveChangesAsync();
            return 0;
        });
        var token = TestTokens.ForUser(_factory, staff);
        var target = await NewPersonAsync();

        foreach (var response in new[]
        {
            await SendAsync(token, HttpMethod.Post, $"/users/{target.Id}/block"),
            await SendAsync(token, HttpMethod.Delete, $"/users/{target.Id}/block"),
            await SendAsync(token, HttpMethod.Get, "/me/blocks"),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("regular_account_required", await AuthApi.ReadCodeAsync(response));
        }
    }

    [Fact]
    public async Task A_blocked_account_cannot_manage_blocks()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();
        await _factory.QueryAsync(async db =>
        {
            typeof(User).GetProperty(nameof(User.Status))!.SetValue(await db.Users.SingleAsync(u => u.Id == alice.Id), UserStatus.Blocked);
            await db.SaveChangesAsync();
            return 0;
        });

        var response = await BlockAsync(alice, bob.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("account_not_active", await AuthApi.ReadCodeAsync(response));
    }

    // ---------- лимит и гонки ----------

    [Fact]
    public async Task At_most_200_blocks_but_repeating_an_existing_one_still_works()
    {
        var alice = await NewPersonAsync();
        var targets = await SeedUsersAsync(201);
        await _factory.QueryAsync(async db =>
        {
            foreach (var id in targets.Take(200))
            {
                db.UserBlocks.Add(UserBlock.Create(alice.Id, id, DateTime.UtcNow));
            }

            await db.SaveChangesAsync();
            return 0;
        });

        var over = await BlockAsync(alice, targets[200]);
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Equal("too_many_blocks", await AuthApi.ReadCodeAsync(over));

        Assert.Equal(HttpStatusCode.NoContent, (await BlockAsync(alice, targets[0])).StatusCode);   // уже заблокирован: повтор допустим
        await UnblockAsync(alice, targets[0]);
        Assert.Equal(HttpStatusCode.NoContent, (await BlockAsync(alice, targets[200])).StatusCode);   // освободилось место
    }

    [Fact]
    public async Task Parallel_blocks_cannot_get_past_the_limit()
    {
        var alice = await NewPersonAsync();
        var targets = await SeedUsersAsync(8);
        var filler = await SeedUsersAsync(197);
        await _factory.QueryAsync(async db =>
        {
            foreach (var id in filler)
            {
                db.UserBlocks.Add(UserBlock.Create(alice.Id, id, DateTime.UtcNow));
            }

            await db.SaveChangesAsync();
            return 0;
        });

        var responses = await Task.WhenAll(targets.Select(t => BlockAsync(alice, t)));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.NoContent));   // осталось 3 места
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));    // остальные — отказ, а не 500
        Assert.Equal(200, await RowsAsync(alice.Id));
    }

    [Fact]
    public async Task Parallel_identical_blocks_all_succeed_and_leave_one_row()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => BlockAsync(alice, bob.Id)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(1, await RowsAsync(alice.Id));
    }

    // ---------- база ----------

    [Fact]
    public async Task The_database_refuses_a_self_block_and_removes_blocks_with_the_user()
    {
        var alice = await NewPersonAsync();
        var bob = await NewPersonAsync();
        await BlockAsync(alice, bob.Id);

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => _factory.QueryAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"UserBlocks\" (\"UserId\", \"BlockedUserId\", \"CreatedAt\") VALUES ({alice.Id}, {alice.Id}, now())");
            return 0;
        }));
        Assert.Contains("CK_UserBlocks_NotSelf", refused.ToString());

        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == bob.Id).ExecuteDeleteAsync();
            return 0;
        });
        Assert.Equal(0, await RowsAsync(alice.Id));   // удаление пользователя убрало и блокировку
    }
}
