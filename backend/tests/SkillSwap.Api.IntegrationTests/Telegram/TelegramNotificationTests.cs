using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Telegram;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Telegram;

/// <summary>Что и когда уходит в Telegram: заявки, уроки и напоминания, сообщения, обращения, переключатели, лимиты и сбои отправки.</summary>
public class TelegramNotificationTests : IClassFixture<TelegramApiFactory>
{
    private readonly ApiFactory _factory;

    public TelegramNotificationTests(TelegramApiFactory factory)
    {
        _factory = factory;
    }

    private static string AllText(IEnumerable<FakeTelegramClient.SentMessage> messages) => string.Join(" | ", messages.Select(m => $"{m.Text} {m.Button?.Url}"));

    private static List<FakeTelegramClient.SentMessage> After(FakeTelegramClient bot, long chat, int skip) => bot.To(chat).Skip(skip).ToList();

    /// <summary>Двое с обменом, оба с подключённым Telegram; возвращает число сообщений на момент старта.</summary>
    private async Task<(TelegramTestApp T, MatchPerson A, MatchPerson B, Guid ExchangeId, long ChatA, long ChatB)> PairAsync(MatchTestWorld? _ = null)
    {
        var t = new TelegramTestApp(_factory);
        var (_, a, b, exchangeId) = await t.ExchangeAsync();
        var chatA = await t.LinkAsync(a);
        var chatB = await t.LinkAsync(b);
        return (t, a, b, exchangeId, chatA, chatB);
    }

    [Fact]
    public async Task A_new_request_and_its_answers_come_in_the_recipients_language_with_a_link_button_and_without_personal_data()
    {
        using var t = new TelegramTestApp(_factory);
        var world = await MatchTestWorld.CreateAsync(_factory);
        var sender = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner), new MatchTestWorld.Options(Name: "Анвар"));
        var receiver = await world.UserAsync((1, SkillLevel.Beginner), (0, SkillLevel.Beginner), new MatchTestWorld.Options(Name: "Мехрона"));
        await t.SetLanguageAsync(sender, "en");
        await t.SetLanguageAsync(receiver, "tg");
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == sender.Id || u.Id == receiver.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var chatSender = await t.LinkAsync(sender);
        var chatReceiver = await t.LinkAsync(receiver);
        var (s0, r0) = (t.Bot.To(chatSender).Count, t.Bot.To(chatReceiver).Count);

        var first = await t.OkAsync(sender, HttpMethod.Post, "/exchange-requests", new { toUserId = receiver.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var received = Assert.Single(After(t.Bot, chatReceiver, r0));
        Assert.Equal("Дархости нави мубодила аз Анвар", received.Text);
        Assert.Equal($"{ApiFactory.FrontendUrl}/requests", received.Button!.Url);
        Assert.Equal("Кушодан", received.Button.Text);

        await t.OkAsync(receiver, HttpMethod.Post, $"/exchange-requests/{first.GetProperty("id").GetGuid()}/decline");
        Assert.Equal("Мехрона declined your exchange request", Assert.Single(After(t.Bot, chatSender, s0)).Text);

        // Повторно тому же после отказа можно не сразу, поэтому принятие проверяется с другим получателем
        var another = await world.UserAsync((1, SkillLevel.Beginner), (0, SkillLevel.Beginner), new MatchTestWorld.Options(Name: "Далер"));
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == another.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var second = await t.OkAsync(sender, HttpMethod.Post, "/exchange-requests", new { toUserId = another.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var accepted = await t.OkAsync(another, HttpMethod.Post, $"/exchange-requests/{second.GetProperty("id").GetGuid()}/accept");
        var yes = After(t.Bot, chatSender, s0)[^1];
        Assert.Equal("Далер accepted your exchange request", yes.Text);
        Assert.Equal($"{ApiFactory.FrontendUrl}/exchanges/{accepted.GetProperty("exchangeId").GetGuid()}", yes.Button!.Url);

        var everything = AllText(t.Bot.To(chatSender).Concat(t.Bot.To(chatReceiver)));
        Assert.DoesNotContain(sender.User.Email!, everything);
        Assert.DoesNotContain(receiver.User.Email!, everything);
    }

    [Fact]
    public async Task A_switched_off_group_is_not_sent_to_telegram_and_switching_back_works()
    {
        using var t = new TelegramTestApp(_factory);
        var world = await MatchTestWorld.CreateAsync(_factory);
        var sender = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        var receiver = await world.UserAsync((1, SkillLevel.Beginner), (0, SkillLevel.Beginner));
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == sender.Id || u.Id == receiver.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var chat = await t.LinkAsync(receiver);
        var settings = await t.OkAsync(receiver, HttpMethod.Put, "/me/notification-settings", new { items = new[] { new { category = "ExchangeRequests", telegram = false, email = false } } });
        Assert.False(settings.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("category").GetString() == "ExchangeRequests").GetProperty("telegram").GetBoolean());
        var before = t.Bot.To(chat).Count;
        var mails = _factory.Mail.To(receiver.User.Email!).Count;

        await t.OkAsync(sender, HttpMethod.Post, "/exchange-requests", new { toUserId = receiver.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });

        Assert.Equal(before, t.Bot.To(chat).Count);
        Assert.Equal(mails, _factory.Mail.To(receiver.User.Email!).Count);   // письма этой группы тоже выключены
    }

    [Fact]
    public async Task The_settings_default_to_on_and_a_group_without_email_cannot_be_given_one()
    {
        using var t = new TelegramTestApp(_factory);
        var (_, a, _, _) = await t.ExchangeAsync();

        var settings = await t.OkAsync(a, HttpMethod.Get, "/me/notification-settings");
        var items = settings.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(5, items.Count);
        Assert.All(items, i => Assert.True(i.GetProperty("telegram").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, items.Single(i => i.GetProperty("category").GetString() == "LessonChanges").GetProperty("email").ValueKind);
        Assert.True(settings.GetProperty("telegramEnabled").GetBoolean());
        Assert.False(settings.GetProperty("telegramConnected").GetBoolean());

        var bad = await t.SendAsync(a, HttpMethod.Put, "/me/notification-settings", new { items = new[] { new { category = "LessonChanges", email = true } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t.SendAsync(a, HttpMethod.Put, "/me/notification-settings", new { items = Array.Empty<object>() })).StatusCode);
    }

    // ---------- уроки ----------

    [Fact]
    public async Task Reminders_come_an_hour_and_ten_minutes_before_once_each()
    {
        var (t, a, b, exchangeId, chatA, chatB) = await PairAsync();
        using var _ = t;
        var start = t.Slot(5);
        await t.LessonAsync(a, b, exchangeId, start);
        var (a0, b0) = (t.Bot.To(chatA).Count, t.Bot.To(chatB).Count);

        Assert.Contains(t.Jobs.Scheduled, j => j.RunAt.UtcDateTime == start.AddMinutes(-60));
        Assert.Contains(t.Jobs.Scheduled, j => j.RunAt.UtcDateTime == start.AddMinutes(-10));

        await t.RunDueAtAsync(start.AddMinutes(-60));
        Assert.Contains("Через 1 час урок с Мехрона", Assert.Single(After(t.Bot, chatA, a0).Where(m => m.Text.Contains("Через 1 час"))).Text);
        Assert.Single(After(t.Bot, chatB, b0).Where(m => m.Text.Contains("Через 1 час")));

        await t.RunDueAtAsync(start.AddMinutes(-10));
        Assert.Single(After(t.Bot, chatA, a0).Where(m => m.Text.Contains("Через 10 минут")));
        Assert.Single(After(t.Bot, chatB, b0).Where(m => m.Text.Contains("Через 10 минут")));

        // Повтор задачи (Hangfire) не присылает второе напоминание
        var sessionId = (await _factory.QueryAsync(db => db.Sessions.AsNoTracking().Where(s => s.ExchangeId == exchangeId).Select(s => s.Id).SingleAsync()));
        using (var scope = t.App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TelegramJobs>().ReminderAsync(sessionId, start.Ticks, 10);
        }

        Assert.Single(After(t.Bot, chatA, a0).Where(m => m.Text.Contains("Через 10 минут")));
    }

    [Fact]
    public async Task A_rescheduled_lesson_tells_the_partner_and_old_reminders_stay_silent_while_new_ones_fire()
    {
        var (t, a, b, exchangeId, chatA, chatB) = await PairAsync();
        using var _ = t;
        var oldStart = t.Slot(10);
        var newStart = t.Slot(30);
        var id = await t.LessonAsync(a, b, exchangeId, oldStart);
        var (a0, b0) = (t.Bot.To(chatA).Count, t.Bot.To(chatB).Count);

        await t.OkAsync(a, HttpMethod.Post, $"/sessions/{id}/reschedule", new { startAt = newStart.ToString("O") });
        var moved = Assert.Single(After(t.Bot, chatB, b0));
        Assert.StartsWith("Анвар переносит урок", moved.Text);

        await t.RunDueAtAsync(oldStart.AddMinutes(-60));   // задачи на старое время ничего не делают
        await t.RunDueAtAsync(oldStart.AddMinutes(-10));
        Assert.DoesNotContain(After(t.Bot, chatA, a0).Concat(After(t.Bot, chatB, b0)), m => m.Text.Contains("урок с"));

        t.Factory.Clock.SetUtcNow(new DateTimeOffset(newStart.AddHours(-12), TimeSpan.Zero));
        await t.OkAsync(b with { Token = TestTokens.ForUser(_factory, b.User) }, HttpMethod.Post, $"/sessions/{id}/confirm");
        await t.RunDueAtAsync(newStart.AddMinutes(-60));
        Assert.Single(After(t.Bot, chatB, b0).Where(m => m.Text.Contains("Через 1 час")));
    }

    [Fact]
    public async Task A_cancelled_lesson_tells_the_partner_and_sends_no_reminders()
    {
        var (t, a, b, exchangeId, chatA, chatB) = await PairAsync();
        using var _ = t;
        var start = t.Slot(6);
        var id = await t.LessonAsync(a, b, exchangeId, start);
        var (a0, b0) = (t.Bot.To(chatA).Count, t.Bot.To(chatB).Count);

        await t.OkAsync(a, HttpMethod.Post, $"/sessions/{id}/cancel");
        var cancelled = Assert.Single(After(t.Bot, chatB, b0));
        Assert.StartsWith("Анвар отменил(а) урок", cancelled.Text);
        Assert.Equal($"{ApiFactory.FrontendUrl}/exchanges/{exchangeId}", cancelled.Button!.Url);

        await t.RunDueAtAsync(start.AddMinutes(-60));
        await t.RunDueAtAsync(start.AddMinutes(-10));
        Assert.Single(After(t.Bot, chatB, b0));
        Assert.Empty(After(t.Bot, chatA, a0).Where(m => m.Text.Contains("урок с")));
    }

    [Fact]
    public async Task A_new_proposal_alone_is_not_sent_to_telegram()
    {
        var (t, a, b, exchangeId, _, chatB) = await PairAsync();
        using var _ = t;
        var before = t.Bot.To(chatB).Count;

        await t.OkAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { startAt = t.Now.AddHours(5).ToString("O"), teacherId = a.Id });

        Assert.DoesNotContain(After(t.Bot, chatB, before), m => !m.Text.StartsWith("Новое сообщение"));
    }

    [Fact]
    public async Task Stopping_the_exchange_tells_both_that_the_scheduled_lesson_is_cancelled()
    {
        var (t, a, b, exchangeId, chatA, chatB) = await PairAsync();
        using var _ = t;
        await t.LessonAsync(a, b, exchangeId, t.Now.AddHours(8));
        var (a0, b0) = (t.Bot.To(chatA).Count, t.Bot.To(chatB).Count);

        await t.OkAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/abort", new { reason = "Больше не получается заниматься" });

        Assert.Single(After(t.Bot, chatA, a0).Where(m => m.Text.Contains("обмен остановлен")));
        Assert.Single(After(t.Bot, chatB, b0).Where(m => m.Text.Contains("обмен остановлен")));
    }

    // ---------- сообщения ----------

    private async Task SayAsync(TelegramTestApp t, MatchPerson who, Guid exchangeId, string text)
    {
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(t.Client.BaseAddress!, "/hubs/chat"), o =>
        {
            o.HttpMessageHandlerFactory = _ => t.App.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            o.AccessTokenProvider = () => Task.FromResult<string?>(who.Token);
        }).Build();
        await hub.StartAsync();
        await hub.InvokeAsync<JsonElement>("Send", exchangeId, text);
    }

    private static async Task WaitUntilOfflineAsync(TelegramTestApp t, MatchPerson who)
    {
        var presence = t.App.Services.GetRequiredService<IUserPresence>();
        for (var i = 0; i < 200 && presence.IsOnline(who.Id); i++)
        {
            await Task.Delay(25);
        }

        Assert.False(presence.IsOnline(who.Id));
    }

    private static List<FakeTelegramClient.SentMessage> NewMessages(FakeTelegramClient bot, long chat) => bot.To(chat).Where(m => m.Text.StartsWith("Новое сообщение")).ToList();

    [Fact]
    public async Task New_messages_come_without_the_text_and_at_most_once_in_five_minutes_per_dialog()
    {
        var (t, a, b, exchangeId, _, chatB) = await PairAsync();
        using var _ = t;
        await WaitUntilOfflineAsync(t, b);

        await SayAsync(t, a, exchangeId, "Секретный текст, мой номер 900-11-22");
        await SayAsync(t, a, exchangeId, "Второе сообщение");

        var first = Assert.Single(NewMessages(t.Bot, chatB));
        Assert.Equal("Новое сообщение от Анвар", first.Text);
        Assert.Equal($"{ApiFactory.FrontendUrl}/exchanges/{exchangeId}", first.Button!.Url);
        Assert.DoesNotContain("Секретный", AllText(t.Bot.To(chatB)));
        Assert.DoesNotContain("900-11-22", AllText(t.Bot.To(chatB)));

        _factory.Clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await SayAsync(t, a with { Token = TestTokens.ForUser(_factory, a.User) }, exchangeId, "Ещё");
        Assert.Single(NewMessages(t.Bot, chatB));
        _factory.Clock.Advance(TimeSpan.FromSeconds(2));
        await SayAsync(t, a with { Token = TestTokens.ForUser(_factory, a.User) }, exchangeId, "Прошло пять минут");
        Assert.Equal(2, NewMessages(t.Bot, chatB).Count);
    }

    [Fact]
    public async Task No_message_goes_to_telegram_while_the_recipient_is_on_the_site()
    {
        var (t, a, b, exchangeId, _, chatB) = await PairAsync();
        using var _ = t;
        await using var online = new HubConnectionBuilder().WithUrl(new Uri(t.Client.BaseAddress!, "/hubs/notifications"), o =>
        {
            o.HttpMessageHandlerFactory = _ => t.App.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            o.AccessTokenProvider = () => Task.FromResult<string?>(b.Token);
        }).Build();
        await online.StartAsync();

        await SayAsync(t, a, exchangeId, "Ты в сети");

        Assert.Empty(NewMessages(t.Bot, chatB));
        await online.StopAsync();
        await WaitUntilOfflineAsync(t, b);
        await SayAsync(t, a, exchangeId, "А теперь вышел");
        Assert.Single(NewMessages(t.Bot, chatB));   // пока он был на сайте, окно 5 минут не расходовалось
    }

    [Fact]
    public async Task The_five_minute_slot_is_given_to_only_one_of_parallel_senders()
    {
        var (t, a, b, exchangeId, _, _) = await PairAsync();
        using var _ = t;
        var now = t.Now;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = t.App.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ITelegramStore>().TryAcquireDialogSlotAsync(b.Id, exchangeId, now, TimeSpan.FromMinutes(5), default);
        }));

        Assert.Equal(1, results.Count(r => r));
    }

    // ---------- обращения ----------

    [Fact]
    public async Task Case_updates_reach_the_participants_without_details()
    {
        var (t, a, b, exchangeId, chatA, chatB) = await PairAsync();
        using var _ = t;
        var (a0, b0) = (t.Bot.To(chatA).Count, t.Bot.To(chatB).Count);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        const string complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";

        var filed = await t.OkAsync(a, HttpMethod.Post, "/cases", new { type = "Conduct", category = "Insults", exchangeId, description = complaint });
        var caseId = filed.GetProperty("id").GetGuid();
        await t.OkAsync(new MatchPerson(moderator.User, moderator.Token), HttpMethod.Post, $"/moderation/cases/{caseId}/take");

        var mine = Assert.Single(After(t.Bot, chatA, a0));
        Assert.Equal("Обращение: взято в работу", mine.Text);
        Assert.Equal($"{ApiFactory.FrontendUrl}/cases/{caseId}", mine.Button!.Url);
        Assert.Single(After(t.Bot, chatB, b0));
        Assert.DoesNotContain("оскорбляет", AllText(t.Bot.To(chatA).Concat(t.Bot.To(chatB))));
    }

    // ---------- лимиты и сбои ----------

    private async Task<(TelegramTestApp T, MatchPerson Receiver, long Chat, Func<Task> Request)> ReceiverAsync()
    {
        var t = new TelegramTestApp(_factory);
        var world = await MatchTestWorld.CreateAsync(_factory);
        var sender = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        var receiver = await world.UserAsync((1, SkillLevel.Beginner), (0, SkillLevel.Beginner));
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == sender.Id || u.Id == receiver.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var chat = await t.LinkAsync(receiver);
        Task Request() => t.OkAsync(sender, HttpMethod.Post, "/exchange-requests", new { toUserId = receiver.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        return (t, receiver, chat, Request);
    }

    [Fact]
    public async Task A_403_from_telegram_disconnects_the_account()
    {
        var (t, receiver, chat, request) = await ReceiverAsync();
        using var _ = t;
        t.Bot.Reply(chat, new TelegramSendResult(TelegramSendStatus.Blocked, Description: "Forbidden: bot was blocked by the user"));

        await request();

        Assert.False(await t.ConnectedAsync(receiver));
        Assert.Equal(0, await _factory.QueryAsync(db => db.TelegramLinks.CountAsync(l => l.UserId == receiver.Id)));
    }

    [Fact]
    public async Task A_429_is_retried_after_the_time_telegram_asks_for()
    {
        var (t, _, chat, request) = await ReceiverAsync();
        using var _ = t;
        var before = t.Bot.To(chat).Count;
        t.Bot.Reply(chat, new TelegramSendResult(TelegramSendStatus.RateLimited, TimeSpan.FromSeconds(30)));

        await request();
        var scheduled = Assert.Single(t.Jobs.Scheduled, j => j.Description.Contains("DeliverAsync"));
        Assert.Equal(t.Now.AddSeconds(31), scheduled.RunAt.UtcDateTime);
        Assert.Equal(before, t.Bot.To(chat).Count);

        await t.RunDueAtAsync(scheduled.RunAt.UtcDateTime);
        Assert.Equal(before + 1, t.Bot.To(chat).Count);
    }

    [Fact]
    public async Task A_message_is_dropped_after_too_many_rate_limits()
    {
        var (t, _, chat, request) = await ReceiverAsync();
        using var _ = t;
        t.Bot.Reply(chat, Enumerable.Repeat(new TelegramSendResult(TelegramSendStatus.RateLimited, TimeSpan.FromSeconds(1)), 20).ToArray());

        await request();
        for (var i = 0; i < 12 && t.Jobs.Scheduled.Any(j => j.Description.Contains("DeliverAsync")); i++)
        {
            await t.RunDueAtAsync(t.Now.AddSeconds(5));
        }

        Assert.DoesNotContain(t.Jobs.Scheduled, j => j.Description.Contains("DeliverAsync"));
        Assert.Empty(t.Jobs.Failures);
    }

    [Fact]
    public async Task A_telegram_outage_fails_the_job_so_the_queue_retries_it_and_a_rejection_does_not()
    {
        var (t, _, chat, request) = await ReceiverAsync();
        using var _ = t;
        t.Bot.Reply(chat, new TelegramSendResult(TelegramSendStatus.Transient, Description: "HTTP 502"), new TelegramSendResult(TelegramSendStatus.Rejected, Description: "chat not found"));

        await request();
        Assert.Contains(t.Jobs.Failures, e => e is TelegramUnavailableException);

        var failures = t.Jobs.Failures.Count;
        using (var scope = t.App.Services.CreateScope())
        {
            var receiverId = await _factory.QueryAsync(db => db.TelegramLinks.Where(l => l.ChatId == chat).Select(l => l.UserId).SingleAsync());
            await scope.ServiceProvider.GetRequiredService<TelegramDeliveryJobs>().DeliverAsync(receiverId, "x", "y", "https://frontend.test", 1);
        }

        Assert.Equal(failures, t.Jobs.Failures.Count);
        Assert.True(await _factory.QueryAsync(db => db.TelegramLinks.AnyAsync(l => l.ChatId == chat)));
    }

    [Fact]
    public async Task Nothing_is_queued_when_the_bot_is_not_configured()
    {
        using var app = _factory.WithSettings(null);
        var client = app.CreateClient();
        var world = await MatchTestWorld.CreateAsync(_factory);
        var sender = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        var receiver = await world.UserAsync((1, SkillLevel.Beginner), (0, SkillLevel.Beginner));
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == sender.Id || u.Id == receiver.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/exchange-requests")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { toUserId = receiver.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sender.Token);
        var sentBefore = _factory.Telegram.Sent.Count;

        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);

        Assert.Equal(sentBefore, _factory.Telegram.Sent.Count);
        var jobs = app.Services.GetRequiredService<SkillSwap.Infrastructure.Jobs.InlineBackgroundJobs>();
        Assert.Empty(jobs.Failures);
    }
}
