using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkillSwap.Api.IntegrationTests.Files;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Account;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>Удаление аккаунта (DELETE /me, раздел 8.1 ТЗ).</summary>
[Collection(ApiCollection.Name)]
public class AccountDeletionTests
{
    private const string Placeholder = "Удалённый пользователь";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private readonly AuthApi _api;

    public AccountDeletionTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _api = new AuthApi(factory);
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private async Task<HttpResponseMessage> SendAsync(MatchPerson? who, HttpMethod method, string url, object? body = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await (client ?? _client).SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    /// <summary>У пользователя из мира подбора нет пароля: задаём его и подтверждаем почту, чтобы он мог войти и удалить аккаунт.</summary>
    private async Task GivePasswordAsync(params MatchPerson[] people)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        foreach (var person in people)
        {
            var user = await users.FindByIdAsync(person.Id.ToString());
            user!.EmailConfirmed = true;
            user.LockoutEnabled = true;   // у пользователей мира, созданных напрямую в БД, блокировка после неверных паролей по умолчанию выключена
            user.SecurityStamp = Guid.NewGuid().ToString("N");   // у пользователей мира подбора, созданных напрямую в БД, штампа нет
            (await users.UpdateAsync(user)).Succeeded.ShouldBeTrue();
            (await users.AddPasswordAsync(user, AuthApi.Password)).Succeeded.ShouldBeTrue();
        }
    }

    private Task<HttpResponseMessage> DeleteAsync(MatchPerson who, object? body = null, HttpClient? client = null) =>
        SendAsync(who, HttpMethod.Delete, "/me", body ?? new { password = AuthApi.Password }, client);

    private Task<User> UserAsync(Guid id) => _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private async Task<Guid> OpenExchangeAsync(MatchTestWorld world, MatchPerson a, MatchPerson b)
    {
        var created = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var accept = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");
        return (await JsonAsync(accept)).GetProperty("exchangeId").GetGuid();
    }

    // ---------- проверки перед удалением ----------

    [Fact]
    public async Task A_wrong_or_missing_password_changes_nothing()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var person = await world.UserAsync(Beginner0, Beginner1);
        await GivePasswordAsync(person);

        var wrong = await DeleteAsync(person, new { password = "not-my-password-1" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("invalid_password", await CodeAsync(wrong));

        var missing = await DeleteAsync(person, new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("validation_failed", await CodeAsync(missing));

        Assert.Equal(UserStatus.Active, (await UserAsync(person.Id)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeleteAsync(null!, new { password = AuthApi.Password })).StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_like_at_login()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var person = await world.UserAsync(Beginner0, Beginner1);
        await GivePasswordAsync(person);

        for (var i = 0; i < 5; i++)
        {
            await DeleteAsync(person, new { password = "wrong-password-" + i });
        }

        var locked = await DeleteAsync(person);   // даже верный пароль уже не принимается
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("account_locked_out", await CodeAsync(locked));
        Assert.Equal(UserStatus.Active, (await UserAsync(person.Id)).Status);
    }

    [Fact]
    public async Task A_staff_account_cannot_be_deleted_this_way()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Moderator);
        await world.ModifyAsync(db => { db.Users.Add(staff); return Task.CompletedTask; });
        var person = new MatchPerson(staff, TestTokens.ForUser(_factory, staff));

        var response = await DeleteAsync(person);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("staff_cannot_delete_account", await CodeAsync(response));
        Assert.Equal(UserStatus.Active, (await UserAsync(staff.Id)).Status);
    }

    [Fact]
    public async Task With_two_factor_the_code_is_required_and_both_app_and_recovery_codes_work()
    {
        // Первый пользователь удаляется кодом приложения, второй резервным.
        foreach (var useRecovery in new[] { false, true })
        {
            var email = await _api.RegisterConfirmedUserAsync();
            var session = await _api.LoginSessionAsync(email);
            var (key, recovery) = await _api.EnableTwoFactorAsync(session.AccessToken);
            var id = (await _factory.QueryAsync(db => db.Users.SingleAsync(u => u.Email == email))).Id;
            var person = new MatchPerson(await UserAsync(id), session.AccessToken);

            var noCode = await DeleteAsync(person);
            Assert.Equal(HttpStatusCode.BadRequest, noCode.StatusCode);
            Assert.Equal("two_factor_code_required", await CodeAsync(noCode));
            var wrong = await DeleteAsync(person, new { password = AuthApi.Password, code = "000000" });
            Assert.Equal("invalid_two_factor_code", await CodeAsync(wrong));
            Assert.Equal(UserStatus.Active, (await UserAsync(id)).Status);

            var ok = useRecovery
                ? await DeleteAsync(person, new { password = AuthApi.Password, code = recovery[0], isRecoveryCode = true })
                : await DeleteAsync(person, new { password = AuthApi.Password, code = _api.TotpCode(key, 0) });

            Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
            Assert.Equal(UserStatus.Deleted, (await UserAsync(id)).Status);
            Assert.False((await UserAsync(id)).TwoFactorEnabled);   // 2FA тоже убрана
        }
    }

    // ---------- что происходит с аккаунтом ----------

    [Fact]
    public async Task Deleting_erases_personal_data_frees_the_email_and_revokes_every_session()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var person = await world.UserAsync(Beginner0, Beginner1);
        await GivePasswordAsync(person);
        var oldEmail = person.User.Email!;
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == person.Id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Bio, "Люблю Python").SetProperty(u => u.UiLanguage, "ru"));
            return 0;
        });
        var session = await _api.LoginSessionAsync(oldEmail);
        var avatar = new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Png()), "file", "a.png" } };
        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/avatar") { Content = avatar };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", person.Token);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(put)).StatusCode);
        var avatarKey = (await UserAsync(person.Id)).AvatarKey!;

        var response = await DeleteAsync(person);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var user = await UserAsync(person.Id);
        Assert.Equal(UserStatus.Deleted, user.Status);
        Assert.NotNull(user.DeletedAt);
        Assert.False(user.ErasurePending);
        Assert.Equal(Placeholder, user.Name);
        Assert.Null(user.Bio);
        Assert.Null(user.DateOfBirth);
        Assert.Null(user.AvatarKey);
        Assert.False(user.IsSearchable);
        Assert.Equal($"deleted-{person.Id:N}@deleted.skillswap.invalid", user.Email);
        Assert.Equal(user.Email, user.UserName);
        Assert.Null(user.PasswordHash);
        Assert.Equal(0, await _factory.QueryAsync(db => db.UserSkills.CountAsync(s => s.UserId == person.Id)));
        Assert.Equal(0, await _factory.QueryAsync(db => db.UserLanguages.CountAsync(l => l.UserId == person.Id)));
        Assert.Equal(0, await _factory.QueryAsync(db => db.Notifications.CountAsync(n => n.UserId == person.Id)));

        using var scope = _factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IFileStorage>().GetAsync(avatarKey, CancellationToken.None));   // аватар удалён из хранилища

        // Сеансы отзываны: ни refresh, ни вход по старому адресу, ни старый access-токен уже не работают.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RefreshAsync(session.RefreshToken)).StatusCode);
        Assert.False((await _api.LoginAsync(oldEmail)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(person, HttpMethod.Get, "/me")).StatusCode);

        // Письмо «аккаунт удалён» ушло на старый адрес на языке пользователя.
        var letter = Assert.Single(_factory.Mail.To(oldEmail), m => m.Subject.Contains("аккаунт удалён"));
        Assert.Contains("Удалённый пользователь", letter.Body);

        // Адрес свободен: на него можно зарегистрироваться заново.
        var again = await _api.RegisterAsync(oldEmail);
        Assert.True(again.IsSuccessStatusCode, await again.Content.ReadAsStringAsync());

        // Повторное удаление невозможно.
        Assert.False((await DeleteAsync(person)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Everything_around_the_account_is_wound_up_in_one_go()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var c = await world.UserAsync(Beginner1, Beginner0);
        var d = await world.UserAsync(Beginner1, Beginner0);
        var e = await world.UserAsync(Beginner1, Beginner0);
        var blocker = await world.UserAsync(Beginner1, Beginner0);
        var blocked = await world.UserAsync(Beginner1, Beginner0);
        await GivePasswordAsync(a);

        var withB = await OpenExchangeAsync(world, a, b);
        var withD = await OpenExchangeAsync(world, a, d);
        // Уроки: запланированный и предложенный в обмене с B, запланированный в обмене с D.
        var scheduled = await ProposeAndAcceptAsync(a, b, withB, Now.AddDays(3));
        var proposed = (await JsonAsync(await SendAsync(a, HttpMethod.Post, $"/exchanges/{withB}/sessions", new { startAt = Now.AddDays(4).ToString("O"), teacherId = a.Id }))).GetProperty("id").GetGuid();
        var withDLesson = await ProposeAndAcceptAsync(a, d, withD, Now.AddDays(5));
        // Заявки: A → C ожидает, E → A ожидает.
        var toC = (await JsonAsync(await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = c.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 }))).GetProperty("id").GetGuid();
        var fromE = (await JsonAsync(await SendAsync(e, HttpMethod.Post, "/exchange-requests",
            new { toUserId = a.Id, toTeachesSkillId = world.S(0), fromTeachesSkillId = world.S(1), durationDays = 30 }))).GetProperty("id").GetGuid();
        // Блокировки в обе стороны и сообщения в чате с B.
        await SendAsync(a, HttpMethod.Post, $"/users/{blocked.Id}/block");
        await SendAsync(blocker, HttpMethod.Post, $"/users/{a.Id}/block");
        await world.ModifyAsync(async db =>
        {
            var cipher = _factory.Services.GetRequiredService<IMessageCipher>();
            db.Messages.Add(Message.CreateText(withB, a.Id, cipher.Encrypt("мой прощальный привет"), false));
            await Task.CompletedTask;
        });
        var notificationsOfB = (await JsonAsync(await SendAsync(b, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items").EnumerateArray()
            .Where(n => n.GetProperty("type").GetString() == "RequestReceived").ToList();
        Assert.Equal(a.User.Name, notificationsOfB.Single().GetProperty("payload").GetProperty("userName").GetString());   // до удаления имя настоящее

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(a)).StatusCode);

        // Обмены прерваны без виновного, причина в базе настоящая, партнёр видит нейтральную.
        foreach (var (exchangeId, partner) in new[] { (withB, b), (withD, d) })
        {
            var row = await _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(x => x.Id == exchangeId));
            Assert.Equal(ExchangeStatus.Aborted, row.Status);
            Assert.Null(row.AbortedById);
            Assert.Equal(Exchange.AbortReasonAccountDeleted, row.AbortReason);

            var view = await JsonAsync(await SendAsync(partner, HttpMethod.Get, $"/exchanges/{exchangeId}"));
            Assert.Equal("Aborted", view.GetProperty("status").GetString());
            Assert.Equal("partner_unavailable", view.GetProperty("abortReason").GetString());
            Assert.Equal(JsonValueKind.Null, view.GetProperty("abortedBy").ValueKind);
            var shown = view.GetProperty("partner");
            Assert.Equal(Placeholder, shown.GetProperty("name").GetString());
            Assert.True(shown.GetProperty("isDeleted").GetBoolean());
            Assert.Equal(JsonValueKind.Null, shown.GetProperty("avatarUrl").ValueKind);

            var told = (await JsonAsync(await SendAsync(partner, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items").EnumerateArray()
                .Single(n => n.GetProperty("type").GetString() == "ExchangeAborted");
            Assert.Equal("partner_unavailable", told.GetProperty("payload").GetProperty("reason").GetString());
            Assert.False(told.GetProperty("payload").TryGetProperty("userName", out _));
        }

        // Будущие уроки отменены без виновного.
        foreach (var id in new[] { scheduled, proposed, withDLesson })
        {
            var session = await _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == id));
            Assert.Equal(SessionStatus.Cancelled, session.Status);
            Assert.Null(session.CancelledById);
        }

        // Ожидающие заявки в обе стороны закрыты.
        foreach (var id in new[] { toC, fromE })
        {
            Assert.Equal(ExchangeRequestStatus.Expired, await _factory.QueryAsync(db => db.ExchangeRequests.Where(r => r.Id == id).Select(r => r.Status).SingleAsync()));
        }

        // Блокировки с его участием убраны.
        Assert.Equal(0, await _factory.QueryAsync(db => db.UserBlocks.CountAsync(x => x.UserId == a.Id || x.BlockedUserId == a.Id)));

        // Его сообщение осталось у партнёра, автор известен только по id, а в заявках и уведомлениях партнёров имя заменено подписью.
        var history = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{withB}/messages"))).GetProperty("items");
        Assert.Contains(history.EnumerateArray(), m => m.GetProperty("text").GetString() == "мой прощальный привет" && m.GetProperty("senderId").GetGuid() == a.Id);
        var request = await JsonAsync(await SendAsync(c, HttpMethod.Get, $"/exchange-requests/{toC}"));
        Assert.Equal(Placeholder, request.GetProperty("fromUser").GetProperty("name").GetString());
        Assert.True(request.GetProperty("fromUser").GetProperty("isDeleted").GetBoolean());
        var accepted = (await JsonAsync(await SendAsync(b, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items").EnumerateArray()
            .Single(n => n.GetProperty("type").GetString() == "RequestReceived");
        Assert.Equal(Placeholder, accepted.GetProperty("payload").GetProperty("userName").GetString());

        // Из подбора и поиска профилей он исчез.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(b, HttpMethod.Get, $"/users/{a.Id}")).StatusCode);
        Assert.DoesNotContain(a.Id, await world.IdsAsync(await world.UserAsync(Beginner1, Beginner0)));

        // Чужие данные не тронуты.
        Assert.Equal(UserStatus.Active, (await UserAsync(b.Id)).Status);
        Assert.NotEqual(Placeholder, (await UserAsync(b.Id)).Name);
        Assert.Single(await _factory.QueryAsync(db => db.UserSkills.Where(s => s.UserId == b.Id && s.Direction == SkillDirection.Teach).ToListAsync()));
    }

    private async Task<Guid> ProposeAndAcceptAsync(MatchPerson proposer, MatchPerson answerer, Guid exchangeId, DateTime start)
    {
        var created = await SendAsync(proposer, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { startAt = start.ToString("O"), teacherId = proposer.Id });
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(answerer, HttpMethod.Post, $"/sessions/{id}/confirm")).StatusCode);
        return id;
    }

    [Fact]
    public async Task The_partner_of_a_deleted_user_is_free_to_start_new_exchanges()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        await GivePasswordAsync(a);
        for (var i = 0; i < 2; i++)
        {
            await OpenExchangeAsync(world, a, await world.UserAsync(Beginner1, Beginner0));
        }

        await OpenExchangeAsync(world, a, b);
        Assert.Equal(3, await _factory.QueryAsync(db => db.Exchanges.CountAsync(x => x.UserAId == a.Id && x.Status == ExchangeStatus.Active)));

        await DeleteAsync(a);

        Assert.Equal(0, await _factory.QueryAsync(db => db.Exchanges.CountAsync(x => x.UserAId == a.Id && x.Status == ExchangeStatus.Active)));   // места в лимите партнёров освободились
    }

    [Fact]
    public async Task Two_simultaneous_deletions_end_in_exactly_one_success_and_no_server_error()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        await GivePasswordAsync(a);
        await OpenExchangeAsync(world, a, b);

        var results = await Task.WhenAll(DeleteAsync(a), DeleteAsync(a), DeleteAsync(a));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.All(results, r => Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode));
        Assert.Equal(UserStatus.Deleted, (await UserAsync(a.Id)).Status);
    }

    // ---------- открытое обращение ----------

    private sealed class AlwaysHold : IErasureHold
    {
        public Task<bool> HasOpenCaseAsync(Guid userId, CancellationToken ct) => Task.FromResult(true);
    }

    [Fact]
    public async Task While_a_case_is_open_personal_data_is_kept_until_it_is_closed()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        await GivePasswordAsync(a);
        var exchangeId = await OpenExchangeAsync(world, a, b);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Bio, "для обращения"));
            return 0;
        });
        using var held = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IErasureHold>();
            services.AddScoped<IErasureHold, AlwaysHold>();
        }));

        var response = await DeleteAsync(a, client: held.CreateClient());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var user = await UserAsync(a.Id);
        Assert.Equal(UserStatus.Deleted, user.Status);
        Assert.True(user.ErasurePending);
        Assert.Equal(a.User.Name, user.Name);              // данные для обращения на месте
        Assert.Equal("для обращения", user.Bio);
        Assert.NotNull(user.DateOfBirth);
        Assert.Equal(2, await _factory.QueryAsync(db => db.UserSkills.CountAsync(s => s.UserId == a.Id)));
        Assert.Equal($"deleted-{a.Id:N}@deleted.skillswap.invalid", user.Email);   // но войти и зарегистрироваться заново можно
        Assert.Null(user.PasswordHash);
        Assert.Equal(ExchangeStatus.Aborted, (await _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(x => x.Id == exchangeId))).Status);

        // Партнёры по-прежнему видят подпись, а не имя.
        var seen = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("partner");
        Assert.Equal(Placeholder, seen.GetProperty("name").GetString());

        // Обращение закрыто: данные стираются; повтор безвреден.
        using (var scope = held.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<AccountDeletionService>();
            await service.CompleteErasureAsync(a.Id, CancellationToken.None);
            await service.CompleteErasureAsync(a.Id, CancellationToken.None);
        }

        var wiped = await UserAsync(a.Id);
        Assert.False(wiped.ErasurePending);
        Assert.Equal(Placeholder, wiped.Name);
        Assert.Null(wiped.Bio);
        Assert.Null(wiped.DateOfBirth);
        Assert.Equal(0, await _factory.QueryAsync(db => db.UserSkills.CountAsync(s => s.UserId == a.Id)));
    }

    [Fact]
    public async Task Completing_the_erasure_of_an_ordinary_user_changes_nothing()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var person = await world.UserAsync(Beginner0, Beginner1);
        using var scope = _factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().CompleteErasureAsync(person.Id, CancellationToken.None);

        var user = await UserAsync(person.Id);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(person.User.Name, user.Name);
    }
}

internal static class AssertExtensions
{
    public static void ShouldBeTrue(this bool value) => Assert.True(value);
}
