using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Chat;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>Уведомления о сообщениях без спама: одно в интерфейсе на обмен, письмо через час и не чаще раза в сутки.</summary>
[Collection(ApiCollection.Name)]
public class NewMessagesNotificationTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public NewMessagesNotificationTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    private async Task<HttpResponseMessage> SendAsync(MatchPerson who, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>A и B с подтверждённой почтой и открытым обменом.</summary>
    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B, Guid ExchangeId)> ExchangeAsync(string languageB = "en")
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id || u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            await db.Users.Where(u => u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.UiLanguage, languageB));
            return 0;
        });
        return (world, a, b, await OpenExchangeAsync(world, a, b));
    }

    private async Task<Guid> OpenExchangeAsync(MatchTestWorld world, MatchPerson a, MatchPerson b)
    {
        var created = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var accept = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");
        return (await JsonAsync(accept)).GetProperty("exchangeId").GetGuid();
    }

    private async Task<HubConnection> ChatAsync(MatchPerson who)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(who.Token);
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static Task<JsonElement> SayAsync(HubConnection connection, Guid exchangeId, string text) =>
        connection.InvokeAsync<JsonElement>("Send", exchangeId, text);

    private async Task<List<JsonElement>> NewMessagesOf(MatchPerson who, bool unreadOnly = false)
    {
        var response = await SendAsync(who, HttpMethod.Get, "/notifications?pageSize=50" + (unreadOnly ? "&unreadOnly=true" : ""));
        return (await JsonAsync(response)).GetProperty("items").EnumerateArray().Where(i => i.GetProperty("type").GetString() == "NewMessages").ToList();
    }

    private Task AgeMessagesAsync(Guid exchangeId, TimeSpan age) => _factory.QueryAsync(async db =>
    {
        var at = _factory.Clock.GetUtcNow().UtcDateTime - age;
        await db.Messages.Where(m => m.ExchangeId == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, at));
        return 0;
    });

    private async Task RunEmailJobAsync(Guid recipientId, Guid exchangeId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<UnreadMessagesJobs>().CheckAsync(recipientId, exchangeId);
    }

    private List<SkillSwap.Api.IntegrationTests.Infrastructure.SentMail> Unread(string email) =>
        _factory.Mail.To(email).Where(m => m.Subject.Contains("unread") || m.Subject.Contains("непрочитанные") || m.Subject.Contains("нохондашуда")).ToList();

    // ---------- уведомление в интерфейсе ----------

    [Fact]
    public async Task The_first_message_creates_one_notification_for_the_recipient_and_the_next_ones_update_it()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var connection = await ChatAsync(a);

        await SayAsync(connection, exchangeId, "раз");
        var first = Assert.Single(await NewMessagesOf(b));
        Assert.Equal(1, first.GetProperty("payload").GetProperty("count").GetInt32());
        Assert.Equal(exchangeId, first.GetProperty("payload").GetProperty("exchangeId").GetGuid());
        Assert.Equal(a.Id, first.GetProperty("payload").GetProperty("userId").GetGuid());
        Assert.Equal(a.User.Name, first.GetProperty("payload").GetProperty("userName").GetString());
        Assert.False(first.GetProperty("isRead").GetBoolean());
        Assert.Empty(await NewMessagesOf(a));   // отправитель о своих сообщениях не уведомляется

        await SayAsync(connection, exchangeId, "два");
        await SayAsync(connection, exchangeId, "три");

        var updated = Assert.Single(await NewMessagesOf(b));   // по-прежнему одно
        Assert.Equal(first.GetProperty("id").GetGuid(), updated.GetProperty("id").GetGuid());
        Assert.Equal(3, updated.GetProperty("payload").GetProperty("count").GetInt32());
        Assert.True(updated.GetProperty("createdAt").GetDateTime() >= first.GetProperty("createdAt").GetDateTime());   // поднялось наверх
        Assert.Single(await NewMessagesOf(b, unreadOnly: true));   // в счётчике значка новые сообщения занимают одну единицу (ещё есть заявка)
        Assert.Equal(3, (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("unreadMessages").GetInt32());
    }

    [Fact]
    public async Task Each_exchange_has_its_own_notification()
    {
        var (world, a, b, first) = await ExchangeAsync();
        var c = await world.UserAsync(Beginner1, Beginner0);
        var second = await OpenExchangeAsync(world, a, c);
        await using var connection = await ChatAsync(a);

        await SayAsync(connection, first, "в первом");
        await SayAsync(connection, second, "во втором");

        Assert.Single(await NewMessagesOf(b));
        Assert.Single(await NewMessagesOf(c));
    }

    [Fact]
    public async Task A_reply_notifies_the_other_side()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var aConn = await ChatAsync(a);
        await using var bConn = await ChatAsync(b);
        await SayAsync(aConn, exchangeId, "вопрос");

        await SayAsync(bConn, exchangeId, "ответ");

        Assert.Single(await NewMessagesOf(a));
        Assert.Single(await NewMessagesOf(b));
    }

    [Fact]
    public async Task Reading_the_chat_marks_the_notification_read_and_the_next_message_creates_a_new_one()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var connection = await ChatAsync(a);
        await SayAsync(connection, exchangeId, "один");
        await SayAsync(connection, exchangeId, "два");
        var firstId = (await NewMessagesOf(b)).Single().GetProperty("id").GetGuid();

        await SendAsync(b, HttpMethod.Post, $"/exchanges/{exchangeId}/messages/read");

        Assert.Empty(await NewMessagesOf(b, unreadOnly: true));
        Assert.True((await NewMessagesOf(b)).Single().GetProperty("isRead").GetBoolean());

        await SayAsync(connection, exchangeId, "три");

        var all = await NewMessagesOf(b);
        Assert.Equal(2, all.Count);
        var fresh = Assert.Single(await NewMessagesOf(b, unreadOnly: true));
        Assert.NotEqual(firstId, fresh.GetProperty("id").GetGuid());
        Assert.Equal(1, fresh.GetProperty("payload").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_partial_read_keeps_the_notification_unread_until_everything_is_read()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var connection = await ChatAsync(a);
        var first = (await SayAsync(connection, exchangeId, "один")).GetProperty("id").GetGuid();
        await SayAsync(connection, exchangeId, "два");

        await SendAsync(b, HttpMethod.Post, $"/exchanges/{exchangeId}/messages/read", new { upToMessageId = first });
        Assert.Single(await NewMessagesOf(b, unreadOnly: true));   // «два» ещё не прочитано

        await SendAsync(b, HttpMethod.Post, $"/exchanges/{exchangeId}/messages/read");
        Assert.Empty(await NewMessagesOf(b, unreadOnly: true));
    }

    [Fact]
    public async Task Dismissing_the_notification_by_hand_does_not_hide_later_messages()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var connection = await ChatAsync(a);
        await SayAsync(connection, exchangeId, "один");
        var id = (await NewMessagesOf(b)).Single().GetProperty("id").GetGuid();
        await SendAsync(b, HttpMethod.Post, $"/notifications/{id}/read");

        await SayAsync(connection, exchangeId, "два");

        var fresh = Assert.Single(await NewMessagesOf(b, unreadOnly: true));
        Assert.NotEqual(id, fresh.GetProperty("id").GetGuid());
        Assert.Equal(2, fresh.GetProperty("payload").GetProperty("count").GetInt32());   // считает все непрочитанные
    }

    [Fact]
    public async Task A_refused_message_creates_no_notification()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var connection = await ChatAsync(a);
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExchangeStatus.Frozen));
            return 0;
        });

        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => SayAsync(connection, exchangeId, "нельзя"));

        Assert.Empty(await NewMessagesOf(b));
    }

    [Fact]
    public async Task Parallel_messages_still_leave_one_unread_notification_with_the_right_count()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var connection = await ChatAsync(a);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => SayAsync(connection, exchangeId, "м" + i)));

        var only = Assert.Single(await NewMessagesOf(b));
        Assert.Equal(10, only.GetProperty("payload").GetProperty("count").GetInt32());
        Assert.Equal(1, await _factory.QueryAsync(db => db.Notifications.CountAsync(n => n.UserId == b.Id && n.Type == NotificationType.NewMessages && !n.IsRead)));
    }

    [Fact]
    public async Task The_notification_is_pushed_to_the_open_tab_and_an_update_keeps_the_same_id()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var pushed = new ConcurrentQueue<JsonElement>();
        await using var chat = await ChatAsync(a);
        await using var notifications = new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/hubs/notifications"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(b.Token);
            })
            .Build();
        notifications.On<JsonElement>("notification", n =>
        {
            if (n.GetProperty("type").GetString() == "NewMessages")
            {
                pushed.Enqueue(n);
            }
        });
        await notifications.StartAsync();

        await SayAsync(chat, exchangeId, "раз");
        await SayAsync(chat, exchangeId, "два");

        var until = DateTime.UtcNow.AddSeconds(15);
        while (pushed.Count < 2 && DateTime.UtcNow < until)
        {
            await Task.Delay(40);
        }

        var items = pushed.ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(items[0].GetProperty("id").GetGuid(), items[1].GetProperty("id").GetGuid());   // клиент заменяет по id
        Assert.Equal(2, items[1].GetProperty("payload").GetProperty("count").GetInt32());
    }

    // ---------- письмо ----------

    [Fact]
    public async Task Only_the_first_unread_message_schedules_the_hour_check()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();
        var before = jobs.Scheduled.Count(j => j.Description.Contains(nameof(UnreadMessagesJobs.CheckAsync)));
        await using var connection = await ChatAsync(a);

        await SayAsync(connection, exchangeId, "раз");
        await SayAsync(connection, exchangeId, "два");
        await SayAsync(connection, exchangeId, "три");

        var after = jobs.Scheduled.Where(j => j.Description.Contains(nameof(UnreadMessagesJobs.CheckAsync))).ToList();
        Assert.Equal(before + 1, after.Count);
        Assert.True(after[^1].RunAt > DateTimeOffset.UtcNow.AddMinutes(55) && after[^1].RunAt < DateTimeOffset.UtcNow.AddMinutes(65));
    }

    [Fact]
    public async Task Nothing_is_sent_before_an_hour_passes_and_the_check_is_rescheduled()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();
        await using var connection = await ChatAsync(a);
        await SayAsync(connection, exchangeId, "свежее");
        await AgeMessagesAsync(exchangeId, TimeSpan.FromMinutes(20));
        var before = jobs.Scheduled.Count;

        await RunEmailJobAsync(b.Id, exchangeId);

        Assert.Empty(Unread(b.User.Email!));
        Assert.True(jobs.Scheduled.Count > before);   // проверка поставлена заново на момент «час с сообщения»
    }

    [Theory]
    [InlineData("en", "unread messages")]
    [InlineData("ru", "непрочитанные сообщения")]
    [InlineData("tg", "нохондашуда")]
    public async Task An_hour_old_unread_message_produces_one_email_in_the_users_language(string language, string subjectPart)
    {
        var (_, a, b, exchangeId) = await ExchangeAsync(language);
        await using var connection = await ChatAsync(a);
        await SayAsync(connection, exchangeId, "секретное содержание 5521");
        await SayAsync(connection, exchangeId, "ещё одно");
        await AgeMessagesAsync(exchangeId, TimeSpan.FromHours(2));

        await RunEmailJobAsync(b.Id, exchangeId);

        var mail = Assert.Single(Unread(b.User.Email!));
        Assert.Contains(subjectPart, mail.Subject);
        Assert.Contains(a.User.Name, mail.Body);
        Assert.Contains("2", mail.Body);                                    // сколько непрочитанных
        Assert.Contains($"/exchanges/{exchangeId}", mail.Body);              // ссылка на обмен
        Assert.DoesNotContain("5521", mail.Body);                            // текст сообщений в письмо не попадает
        Assert.DoesNotContain("секретное", mail.Body);
        Assert.Empty(Unread(a.User.Email!));                                 // автору письма нет
    }

    [Fact]
    public async Task If_the_message_was_read_within_the_hour_there_is_no_email()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var connection = await ChatAsync(a);
        await SayAsync(connection, exchangeId, "прочитают");
        await AgeMessagesAsync(exchangeId, TimeSpan.FromHours(2));
        await SendAsync(b, HttpMethod.Post, $"/exchanges/{exchangeId}/messages/read");

        await RunEmailJobAsync(b.Id, exchangeId);

        Assert.Empty(Unread(b.User.Email!));
    }

    [Fact]
    public async Task A_deleted_unread_message_does_not_cause_an_email()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var connection = await ChatAsync(a);
        var id = (await SayAsync(connection, exchangeId, "ошибся")).GetProperty("id").GetGuid();
        await connection.InvokeAsync<JsonElement>("Delete", id);
        await AgeMessagesAsync(exchangeId, TimeSpan.FromHours(2));

        await RunEmailJobAsync(b.Id, exchangeId);

        Assert.Empty(Unread(b.User.Email!));
    }

    [Fact]
    public async Task The_same_unread_messages_are_mailed_only_once()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var connection = await ChatAsync(a);
        await SayAsync(connection, exchangeId, "раз");
        await AgeMessagesAsync(exchangeId, TimeSpan.FromHours(3));

        await RunEmailJobAsync(b.Id, exchangeId);
        await RunEmailJobAsync(b.Id, exchangeId);
        await RunEmailJobAsync(b.Id, exchangeId);

        Assert.Single(Unread(b.User.Email!));
    }

    [Fact]
    public async Task At_most_one_email_per_day_per_exchange_then_new_unread_messages_get_the_next_one()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();
        await using var connection = await ChatAsync(a);
        await SayAsync(connection, exchangeId, "раз");
        await AgeMessagesAsync(exchangeId, TimeSpan.FromHours(5));
        await RunEmailJobAsync(b.Id, exchangeId);
        Assert.Single(Unread(b.User.Email!));

        // Письмо «ушло» 3 часа назад по часам приложения; новое сообщение пришло после него и тоже висит больше часа, но суток ещё нет.
        var clockNow = _factory.Clock.GetUtcNow().UtcDateTime;
        await _factory.QueryAsync(async db =>
        {
            await db.Notifications.Where(n => n.UserId == b.Id && n.ExchangeId == exchangeId && n.EmailQueuedAt != null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.EmailQueuedAt, clockNow.AddHours(-3)));
            return 0;
        });
        var second = (await SayAsync(connection, exchangeId, "два")).GetProperty("id").GetGuid();
        await _factory.QueryAsync(async db =>
        {
            await db.Messages.Where(m => m.Id == second).ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, _factory.Clock.GetUtcNow().UtcDateTime.AddHours(-2)));
            return 0;
        });
        var scheduledBefore = jobs.Scheduled.Count;
        await RunEmailJobAsync(b.Id, exchangeId);

        Assert.Single(Unread(b.User.Email!));                 // суточный лимит
        Assert.True(jobs.Scheduled.Count > scheduledBefore);   // отложено до конца суток, а не потеряно

        // Прошли сутки с прошлого письма.
        await _factory.QueryAsync(async db =>
        {
            await db.Notifications.Where(n => n.UserId == b.Id && n.ExchangeId == exchangeId && n.EmailQueuedAt != null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.EmailQueuedAt, _factory.Clock.GetUtcNow().UtcDateTime.AddHours(-25)));
            return 0;
        });
        await RunEmailJobAsync(b.Id, exchangeId);

        Assert.Equal(2, Unread(b.User.Email!).Count);
    }

    [Fact]
    public async Task Parallel_checks_send_exactly_one_email()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var connection = await ChatAsync(a);
        await SayAsync(connection, exchangeId, "раз");
        await AgeMessagesAsync(exchangeId, TimeSpan.FromHours(2));

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RunEmailJobAsync(b.Id, exchangeId)));

        Assert.Single(Unread(b.User.Email!));
    }

    [Fact]
    public async Task No_email_goes_to_an_unconfirmed_address_or_for_an_exchange_without_messages()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await RunEmailJobAsync(b.Id, exchangeId);   // сообщений нет
        Assert.Empty(Unread(b.User.Email!));

        await using var connection = await ChatAsync(a);
        await SayAsync(connection, exchangeId, "раз");
        await AgeMessagesAsync(exchangeId, TimeSpan.FromHours(2));
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, false));
            return 0;
        });

        await RunEmailJobAsync(b.Id, exchangeId);

        Assert.Empty(Unread(b.User.Email!));
    }

    [Fact]
    public async Task The_email_job_ignores_unknown_and_foreign_ids()
    {
        var (world, _, b, exchangeId) = await ExchangeAsync();
        var stranger = await world.UserAsync(Beginner0, Beginner1);

        await RunEmailJobAsync(Guid.NewGuid(), exchangeId);
        await RunEmailJobAsync(b.Id, Guid.NewGuid());
        await RunEmailJobAsync(stranger.Id, exchangeId);

        Assert.Empty(Unread(b.User.Email!));
    }
}
