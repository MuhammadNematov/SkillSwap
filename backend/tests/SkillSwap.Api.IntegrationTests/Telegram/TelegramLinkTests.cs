using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Telegram;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Telegram;

/// <summary>Подключение и отключение Telegram, webhook с секретом, выключенная функция.</summary>
public class TelegramLinkTests : IClassFixture<TelegramApiFactory>
{
    private readonly ApiFactory _factory;

    public TelegramLinkTests(TelegramApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<MatchPerson> UserAsync(string language = "ru")
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var user = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.UiLanguage, language));
            return 0;
        });
        return user;
    }

    private Task<TelegramLink?> LinkRowAsync(MatchPerson who) => _factory.QueryAsync(db => db.TelegramLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == who.Id));

    [Theory]
    [InlineData("ru", "Telegram подключён")]
    [InlineData("tg", "пайваст шуд")]
    [InlineData("en", "Telegram is connected")]
    public async Task A_correct_code_connects_the_chat_and_the_bot_answers_in_the_users_language(string language, string expected)
    {
        using var t = new TelegramTestApp(_factory);
        var user = await UserAsync(language);
        Assert.False(await t.ConnectedAsync(user));

        var code = await t.CodeAsync(user);
        var stored = await _factory.QueryAsync(db => db.TelegramLinkCodes.AsNoTracking().SingleAsync(c => c.UserId == user.Id));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))), stored.CodeHash);   // в базе только хеш
        Assert.DoesNotContain(code, stored.CodeHash);
        Assert.Equal(t.Now.AddMinutes(10), stored.ExpiresAt, TimeSpan.FromSeconds(1));

        var chat = TelegramTestApp.NextChat();
        await t.MessageAsync(chat, $"/start {code}");

        Assert.True(await t.ConnectedAsync(user));
        Assert.Equal(chat, (await LinkRowAsync(user))!.ChatId);
        Assert.Contains(expected, Assert.Single(t.Bot.To(chat)).Text);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        using var t = new TelegramTestApp(_factory);
        var user = await UserAsync();
        var code = await t.CodeAsync(user);
        _factory.Clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        var chat = TelegramTestApp.NextChat();

        await t.MessageAsync(chat, $"/start {code}");

        Assert.Null(await LinkRowAsync(user));
        Assert.Contains("истекла", Assert.Single(t.Bot.To(chat)).Text);
    }

    [Fact]
    public async Task A_code_works_once_another_chat_cannot_reuse_it_and_a_redelivery_from_the_same_chat_is_harmless()
    {
        using var t = new TelegramTestApp(_factory);
        var user = await UserAsync();
        var code = await t.CodeAsync(user);
        var chat = TelegramTestApp.NextChat();
        var stranger = TelegramTestApp.NextChat();
        await t.MessageAsync(chat, $"/start {code}");

        await t.MessageAsync(stranger, $"/start {code}");
        await t.MessageAsync(chat, $"/start {code}");   // webhook доставил то же ещё раз

        Assert.Equal(chat, (await LinkRowAsync(user))!.ChatId);
        Assert.Contains("недействительна", Assert.Single(t.Bot.To(stranger)).Text);
        Assert.All(t.Bot.To(chat), m => Assert.Contains("Telegram подключён", m.Text));
    }

    [Fact]
    public async Task An_unknown_code_is_refused_and_a_code_always_links_its_owner_not_someone_else()
    {
        using var t = new TelegramTestApp(_factory);
        var owner = await UserAsync();
        var other = await UserAsync();
        var chat = TelegramTestApp.NextChat();

        await t.MessageAsync(chat, "/start vydumannyi-kod-123456789012345");
        Assert.Null(await LinkRowAsync(owner));

        await t.MessageAsync(chat, $"/start {await t.CodeAsync(owner)}");   // код владельца, пришедший из «чужого» чата, привязывает только владельца

        Assert.NotNull(await LinkRowAsync(owner));
        Assert.False(await t.ConnectedAsync(other));
    }

    [Fact]
    public async Task A_new_code_cancels_the_previous_one()
    {
        using var t = new TelegramTestApp(_factory);
        var user = await UserAsync();
        var first = await t.CodeAsync(user);
        var second = await t.CodeAsync(user);
        var chat = TelegramTestApp.NextChat();

        await t.MessageAsync(chat, $"/start {first}");
        Assert.Null(await LinkRowAsync(user));
        await t.MessageAsync(chat, $"/start {second}");

        Assert.Equal(chat, (await LinkRowAsync(user))!.ChatId);
    }

    [Fact]
    public async Task Linking_a_chat_to_another_account_moves_the_link_and_is_written_to_the_audit()
    {
        using var t = new TelegramTestApp(_factory);
        var first = await UserAsync();
        var second = await UserAsync();
        var chat = await t.LinkAsync(first);

        await t.MessageAsync(chat, $"/start {await t.CodeAsync(second)}");

        Assert.Null(await LinkRowAsync(first));
        Assert.Equal(chat, (await LinkRowAsync(second))!.ChatId);
        var audit = await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "TelegramLinkMoved" && a.ActorId == second.Id));
        Assert.Equal(first.Id, audit.TargetId);
        Assert.DoesNotContain(chat.ToString(), audit.DetailsJson ?? string.Empty);   // идентификатор чата в аудит не пишется
    }

    [Fact]
    public async Task Connecting_a_new_chat_replaces_the_old_one_for_the_account()
    {
        using var t = new TelegramTestApp(_factory);
        var user = await UserAsync();
        await t.LinkAsync(user);

        var newChat = await t.LinkAsync(user);

        Assert.Equal(1, await _factory.QueryAsync(db => db.TelegramLinks.CountAsync(l => l.UserId == user.Id)));
        Assert.Equal(newChat, (await LinkRowAsync(user))!.ChatId);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("supergroup")]
    [InlineData("channel")]
    public async Task Only_private_chats_can_be_connected(string chatType)
    {
        using var t = new TelegramTestApp(_factory);
        var user = await UserAsync();
        var chat = TelegramTestApp.NextChat();

        await t.MessageAsync(chat, $"/start {await t.CodeAsync(user)}", chatType);

        Assert.Null(await LinkRowAsync(user));
        Assert.Empty(t.Bot.To(chat));
    }

    [Fact]
    public async Task Stop_in_the_bot_and_disconnect_on_the_site_both_remove_the_link()
    {
        using var t = new TelegramTestApp(_factory);
        var user = await UserAsync();
        var chat = await t.LinkAsync(user);

        await t.MessageAsync(chat, "/stop");
        Assert.Null(await LinkRowAsync(user));
        Assert.Contains("отключён", t.Bot.To(chat)[^1].Text);
        await t.MessageAsync(chat, "/stop");   // повтор не ошибка
        Assert.Contains("не подключён", t.Bot.To(chat)[^1].Text);

        await t.LinkAsync(user);
        var status = await t.OkAsync(user, HttpMethod.Delete, "/me/telegram");
        Assert.False(status.GetProperty("connected").GetBoolean());
        Assert.Null(await LinkRowAsync(user));
    }

    [Fact]
    public async Task Staff_accounts_cannot_connect_telegram()
    {
        using var t = new TelegramTestApp(_factory);
        var staff = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);

        var response = await t.SendAsync(new MatchPerson(staff.User, staff.Token), HttpMethod.Post, "/me/telegram/link");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("regular_account_required", (await TelegramTestApp.JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Deleting_the_account_removes_the_link_the_codes_and_the_settings()
    {
        using var t = new TelegramTestApp(_factory);
        var api = new AuthApi(_factory);
        var email = await api.RegisterConfirmedUserAsync();
        var (access, _) = await api.LoginTokensAsync(email);
        var user = new MatchPerson(await _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Email == email)), access);
        await t.LinkAsync(user);
        await t.OkAsync(user, HttpMethod.Put, "/me/notification-settings", new { items = new[] { new { category = "Messages", telegram = false } } });

        var response = await t.SendAsync(user, HttpMethod.Delete, "/me", new { password = AuthApi.Password });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await _factory.QueryAsync(db => db.TelegramLinks.CountAsync(l => l.UserId == user.Id)));
        Assert.Equal(0, await _factory.QueryAsync(db => db.TelegramLinkCodes.CountAsync(l => l.UserId == user.Id)));
        Assert.Equal(0, await _factory.QueryAsync(db => db.NotificationPreferences.CountAsync(l => l.UserId == user.Id)));
    }

    // ---------- webhook ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-secret-0000000000000000")]
    [InlineData("integration-webhook-secret-12345")]
    public async Task The_webhook_refuses_a_missing_or_wrong_secret_header(string? secret)
    {
        using var t = new TelegramTestApp(_factory);
        var user = await UserAsync();
        var chat = TelegramTestApp.NextChat();

        var response = await t.WebhookRawAsync(secret, TelegramTestApp.Update(chat, $"/start {await t.CodeAsync(user)}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(await LinkRowAsync(user));
        Assert.Empty(t.Bot.To(chat));
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("{}")]
    public async Task The_webhook_refuses_a_body_that_is_not_an_update(string body)
    {
        using var t = new TelegramTestApp(_factory);

        var response = await t.WebhookRawAsync(TelegramTestApp.Secret, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_webhook_does_not_exist_in_development_even_with_the_right_secret()
    {
        using var t = new TelegramTestApp(_factory, "Development");

        var response = await t.WebhookRawAsync(TelegramTestApp.Secret, TelegramTestApp.Update(1, "/stop"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- выключенная функция ----------

    [Fact]
    public async Task Without_a_token_the_feature_is_off_and_the_application_works()
    {
        var client = _factory.CreateClient();
        var user = await UserAsync();
        HttpRequestMessage Request(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, "/api/v1" + url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", user.Token);
            return request;
        }

        var status = await (await client.SendAsync(Request(HttpMethod.Get, "/me/telegram"))).Content.ReadAsStringAsync();
        var link = await client.SendAsync(Request(HttpMethod.Post, "/me/telegram/link"));
        var settings = await (await client.SendAsync(Request(HttpMethod.Get, "/me/notification-settings"))).Content.ReadAsStringAsync();
        var hook = await client.PostAsync("/api/v1/webhooks/telegram", new StringContent("{}"));

        Assert.Contains("\"enabled\":false", status);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, link.StatusCode);
        Assert.Contains("telegram_not_configured", await link.Content.ReadAsStringAsync());
        Assert.Contains("\"telegramEnabled\":false", settings);
        Assert.Equal(HttpStatusCode.NotFound, hook.StatusCode);
    }

    // ---------- запуск ----------

    [Theory]
    [InlineData("", "")]
    [InlineData("short", "")]
    [InlineData("integration-webhook-secret-123456", "http://insecure.example.com")]
    public void Outside_development_a_configured_bot_without_a_strong_secret_or_https_address_does_not_start(string secret, string baseUrl)
    {
        using var app = _factory.WithSettings("Production",
            [.. ApiFactory.TelegramOn, ("Telegram:WebhookSecret", secret), ("Telegram:WebhookBaseUrl", baseUrl), ("Hangfire:Enabled", "false")]);

        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        Assert.Contains("Telegram", error.ToString());
        Assert.DoesNotContain("TEST-token-never-real", error.ToString());
    }

    [Fact]
    public async Task Outside_development_the_webhook_is_registered_at_start_with_the_secret()
    {
        using var t = new TelegramTestApp(_factory, "Production", ("Telegram:WebhookBaseUrl", "https://api.skillswap.test"));
        var expected = ("https://api.skillswap.test/api/v1/webhooks/telegram", TelegramTestApp.Secret);

        for (var i = 0; i < 100 && !t.Bot.Webhooks.Contains(expected); i++)
        {
            await Task.Delay(50);
        }

        Assert.Contains(expected, t.Bot.Webhooks);
    }

    [Fact]
    public async Task In_development_long_polling_receives_start_and_no_webhook_is_set()
    {
        using var t = new TelegramTestApp(_factory, "Development");
        var user = await UserAsync();
        var code = await t.CodeAsync(user);
        var chat = TelegramTestApp.NextChat();

        t.Bot.PushUpdate(new TelegramUpdate(50_000, new TelegramIncomingMessage(chat, true, $"/start {code}", "ru")));

        for (var i = 0; i < 200 && await LinkRowAsync(user) is null; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(chat, (await LinkRowAsync(user))?.ChatId);
        for (var i = 0; i < 100 && !t.Bot.RequestedOffsets.Contains(50_001); i++)
        {
            await Task.Delay(50);
        }

        Assert.Contains(50_001, t.Bot.RequestedOffsets);   // обработанное обновление больше не запрашивается
        Assert.True(t.Bot.DeletedWebhooks > 0);
        Assert.DoesNotContain(t.Bot.Webhooks, w => w.Url.Contains("localhost"));
    }
}
