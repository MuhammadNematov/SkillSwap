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
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Notifications;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>Уведомления (раздел 12 ТЗ): события заявок и обменов, список, «прочитано», хаб в реальном времени, письма-дубли только важного.</summary>
[Collection(ApiCollection.Name)]
public class NotificationTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public NotificationTests(ApiFactory factory)
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

    /// <summary>A умеет навык 0 и учится навыку 1, B наоборот. Почта подтверждена (письма идут только на подтверждённые адреса).</summary>
    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B)> PairAsync(string languageB = "en")
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
        return (world, a, b);
    }

    private async Task<Guid> RequestAsync(MatchTestWorld world, MatchPerson from, MatchPerson to)
    {
        var response = await SendAsync(from, HttpMethod.Post, "/exchange-requests",
            new { toUserId = to.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<(Guid RequestId, Guid ExchangeId)> ExchangeAsync(MatchTestWorld world, MatchPerson a, MatchPerson b)
    {
        var requestId = await RequestAsync(world, a, b);
        var accept = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");
        return (requestId, (await JsonAsync(accept)).GetProperty("exchangeId").GetGuid());
    }

    private async Task<List<JsonElement>> NotificationsAsync(MatchPerson who, string query = "")
    {
        var response = await SendAsync(who, HttpMethod.Get, "/notifications" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("items").EnumerateArray().ToList();
    }

    private static List<JsonElement> OfType(IEnumerable<JsonElement> items, string type) =>
        items.Where(i => i.GetProperty("type").GetString() == type).ToList();

    // ---------- события ----------

    [Fact]
    public async Task A_new_request_notifies_the_recipient_only()
    {
        var (world, a, b) = await PairAsync();

        var requestId = await RequestAsync(world, a, b);

        var received = Assert.Single(OfType(await NotificationsAsync(b), "RequestReceived"));
        Assert.False(received.GetProperty("isRead").GetBoolean());
        var payload = received.GetProperty("payload");
        Assert.Equal(requestId, payload.GetProperty("requestId").GetGuid());
        Assert.Equal(a.Id, payload.GetProperty("userId").GetGuid());
        Assert.Equal(a.User.Name, payload.GetProperty("userName").GetString());
        Assert.Equal(30, payload.GetProperty("durationDays").GetInt32());
        Assert.Empty(await NotificationsAsync(a));   // отправитель о собственной заявке не уведомляется
    }

    [Fact]
    public async Task A_refused_request_creates_no_notification()
    {
        var (world, a, b) = await PairAsync();

        var refused = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(0), durationDays = 30 });   // b не преподаёт навык 0

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(await NotificationsAsync(b));
    }

    [Fact]
    public async Task Accepting_notifies_the_sender_with_the_exchange_id()
    {
        var (world, a, b) = await PairAsync();

        var (requestId, exchangeId) = await ExchangeAsync(world, a, b);

        var accepted = Assert.Single(OfType(await NotificationsAsync(a), "RequestAccepted"));
        Assert.Equal(exchangeId, accepted.GetProperty("payload").GetProperty("exchangeId").GetGuid());
        Assert.Equal(requestId, accepted.GetProperty("payload").GetProperty("requestId").GetGuid());
        Assert.Equal(b.User.Name, accepted.GetProperty("payload").GetProperty("userName").GetString());
        Assert.Empty(OfType(await NotificationsAsync(b), "RequestAccepted"));
    }

    [Fact]
    public async Task A_failed_accept_leaves_no_notification_behind()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync([Beginner0, (2, SkillLevel.Beginner)], [Beginner1]);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var response = await SendAsync(a, HttpMethod.Post, "/exchange-requests", new { toUserId = b.Id, toTeachesSkillId = world.S(1), durationDays = 30 });
        var requestId = (await JsonAsync(response)).GetProperty("id").GetGuid();

        var refused = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");   // нужен выбор навыка

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Empty(OfType(await NotificationsAsync(a), "RequestAccepted"));
    }

    [Fact]
    public async Task Declining_notifies_the_sender()
    {
        var (world, a, b) = await PairAsync();
        var requestId = await RequestAsync(world, a, b);

        await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/decline");

        var declined = Assert.Single(OfType(await NotificationsAsync(a), "RequestDeclined"));
        Assert.Equal(requestId, declined.GetProperty("payload").GetProperty("requestId").GetGuid());
    }

    [Fact]
    public async Task Expiry_notifies_the_sender_once_even_if_the_job_runs_twice()
    {
        var (world, a, b) = await PairAsync();
        var requestId = await RequestAsync(world, a, b);
        await _factory.QueryAsync(async db =>
        {
            await db.ExchangeRequests.Where(r => r.Id == requestId).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, _factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1)));
            return 0;
        });

        for (var i = 0; i < 2; i++)
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ExchangeRequestJobs>().ExpireAsync(requestId);
        }

        Assert.Single(OfType(await NotificationsAsync(a), "RequestExpired"));
    }

    [Fact]
    public async Task Withdrawing_and_a_block_do_not_add_notifications_that_could_reveal_the_block()
    {
        var (world, a, b) = await PairAsync();
        var requestId = await RequestAsync(world, a, b);
        var before = (await NotificationsAsync(a)).Count;

        await SendAsync(b, HttpMethod.Post, $"/users/{a.Id}/block");   // получатель блокирует отправителя: заявка Expired

        Assert.Equal(before, (await NotificationsAsync(a)).Count);   // отправитель ничего не получает
        Assert.Equal(ExchangeRequestStatus.Expired, await _factory.QueryAsync(db => db.ExchangeRequests.Where(r => r.Id == requestId).Select(r => r.Status).SingleAsync()));
    }

    [Fact]
    public async Task A_block_that_aborts_an_exchange_notifies_only_the_blocked_with_the_neutral_reason()
    {
        var (world, a, b) = await PairAsync();
        var (_, exchangeId) = await ExchangeAsync(world, a, b);

        await SendAsync(a, HttpMethod.Post, $"/users/{b.Id}/block");

        var toBlocked = Assert.Single(OfType(await NotificationsAsync(b), "ExchangeAborted"));
        Assert.Equal("partner_unavailable", toBlocked.GetProperty("payload").GetProperty("reason").GetString());
        Assert.Equal(exchangeId, toBlocked.GetProperty("payload").GetProperty("exchangeId").GetGuid());
        Assert.False(toBlocked.GetProperty("payload").TryGetProperty("userName", out _));   // имя блокировавшего не раскрывается
        Assert.Empty(OfType(await NotificationsAsync(a), "ExchangeAborted"));
    }

    [Fact]
    public async Task Finishing_notifies_the_partner_once_then_both_when_completed()
    {
        var (world, a, b) = await PairAsync();
        var (_, exchangeId) = await ExchangeAsync(world, a, b);

        await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/complete");
        await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/complete");   // повтор не дублирует уведомление

        var asked = Assert.Single(OfType(await NotificationsAsync(b), "ExchangeFinishRequested"));
        Assert.Equal(a.User.Name, asked.GetProperty("payload").GetProperty("userName").GetString());

        await SendAsync(b, HttpMethod.Post, $"/exchanges/{exchangeId}/complete");

        Assert.Single(OfType(await NotificationsAsync(a), "ExchangeCompleted"));
        Assert.Single(OfType(await NotificationsAsync(b), "ExchangeCompleted"));
    }

    [Fact]
    public async Task Aborting_notifies_the_partner_with_the_reason()
    {
        var (world, a, b) = await PairAsync();
        var (_, exchangeId) = await ExchangeAsync(world, a, b);

        await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/abort", new { reason = "Нет времени" });

        var aborted = Assert.Single(OfType(await NotificationsAsync(b), "ExchangeAborted"));
        Assert.Equal("Нет времени", aborted.GetProperty("payload").GetProperty("reason").GetString());
        Assert.Equal(a.User.Name, aborted.GetProperty("payload").GetProperty("userName").GetString());
        Assert.Empty(OfType(await NotificationsAsync(a), "ExchangeAborted"));
    }

    [Fact]
    public async Task The_extension_notifies_the_partner_and_then_both()
    {
        var (world, a, b) = await PairAsync();
        var (_, exchangeId) = await ExchangeAsync(world, a, b);

        await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/extend");
        Assert.Single(OfType(await NotificationsAsync(b), "ExtensionProposed"));

        await SendAsync(b, HttpMethod.Post, $"/exchanges/{exchangeId}/extend");

        Assert.Single(OfType(await NotificationsAsync(a), "ExchangeExtended"));
        Assert.Single(OfType(await NotificationsAsync(b), "ExchangeExtended"));
    }

    [Fact]
    public async Task The_automatic_completion_notifies_both()
    {
        var (world, a, b) = await PairAsync();
        var (_, exchangeId) = await ExchangeAsync(world, a, b);
        await _factory.QueryAsync(async db =>
        {
            var end = _factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
            await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.EndDate, end).SetProperty(e => e.StartDate, end.AddDays(-30)));
            return 0;
        });

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ExchangeJobs>().CompleteDueAsync(exchangeId);
        }

        Assert.Single(OfType(await NotificationsAsync(a), "ExchangeCompleted"));
        Assert.Single(OfType(await NotificationsAsync(b), "ExchangeCompleted"));
    }

    // ---------- список и «прочитано» ----------

    [Fact]
    public async Task The_list_counts_unread_filters_and_pages_newest_first()
    {
        var (world, a, b) = await PairAsync();
        await ExchangeAsync(world, a, b);                      // a получает RequestAccepted
        await SendAsync(b, HttpMethod.Post, $"/users/{a.Id}/block");   // для a новых уведомлений нет
        var (world2, a2, b2) = await PairAsync();
        await RequestAsync(world2, a2, b2);
        var other = await world2.UserAsync(Beginner0, Beginner1);
        await RequestAsync(world2, other, b2);                 // у b2 два входящих

        var list = await JsonAsync(await SendAsync(b2, HttpMethod.Get, "/notifications?pageSize=1"));

        Assert.Equal(2, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, list.GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, list.GetProperty("items").GetArrayLength());
        var first = list.GetProperty("items")[0].GetProperty("createdAt").GetDateTime();
        var second = (await JsonAsync(await SendAsync(b2, HttpMethod.Get, "/notifications?pageSize=1&page=2"))).GetProperty("items")[0].GetProperty("createdAt").GetDateTime();
        Assert.True(first >= second);

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(b2, HttpMethod.Get, "/notifications?pageSize=500")).StatusCode);
        Assert.Equal(2, (await JsonAsync(await SendAsync(b2, HttpMethod.Get, "/notifications/unread-count"))).GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task Reading_marks_one_notification_and_repeats_are_harmless()
    {
        var (world, a, b) = await PairAsync();
        await RequestAsync(world, a, b);
        var id = (await NotificationsAsync(b))[0].GetProperty("id").GetGuid();

        var first = await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/notifications/{id}/read"));
        var readAt = first.GetProperty("readAt").GetDateTime();
        var again = await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/notifications/{id}/read"));

        Assert.True(first.GetProperty("isRead").GetBoolean());
        Assert.True(Math.Abs((readAt - again.GetProperty("readAt").GetDateTime()).TotalMilliseconds) < 1);   // время первого прочтения не затирается
        Assert.Empty(await NotificationsAsync(b, "?unreadOnly=true"));
        Assert.Single(await NotificationsAsync(b));                         // без фильтра запись осталась
        Assert.Equal(0, (await JsonAsync(await SendAsync(b, HttpMethod.Get, "/notifications"))).GetProperty("unreadCount").GetInt32());
    }

    [Fact]
    public async Task Someone_elses_or_unknown_notification_is_not_found()
    {
        var (world, a, b) = await PairAsync();
        await RequestAsync(world, a, b);
        var id = (await NotificationsAsync(b))[0].GetProperty("id").GetGuid();

        foreach (var target in new[] { id, Guid.NewGuid() })
        {
            var response = await SendAsync(a, HttpMethod.Post, $"/notifications/{target}/read");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("notification_not_found", (await JsonAsync(response)).GetProperty("code").GetString());
        }

        Assert.False((await NotificationsAsync(b))[0].GetProperty("isRead").GetBoolean());   // чужая попытка ничего не отметила
    }

    [Fact]
    public async Task Read_all_marks_only_my_notifications()
    {
        var (world, a, b) = await PairAsync();
        await RequestAsync(world, a, b);
        var c = await world.UserAsync(Beginner0, Beginner1);
        await RequestAsync(world, c, b);
        var (world2, a2, b2) = await PairAsync();
        await RequestAsync(world2, a2, b2);

        var response = await JsonAsync(await SendAsync(b, HttpMethod.Post, "/notifications/read-all"));

        Assert.Equal(2, response.GetProperty("marked").GetInt32());
        Assert.Empty(await NotificationsAsync(b, "?unreadOnly=true"));
        Assert.Single(await NotificationsAsync(b2, "?unreadOnly=true"));   // у других пользователей ничего не тронуто
        Assert.Equal(0, (await JsonAsync(await SendAsync(b, HttpMethod.Post, "/notifications/read-all"))).GetProperty("marked").GetInt32());
    }

    [Fact]
    public async Task Unauthenticated_requests_are_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/v1/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.PostAsync("/api/v1/notifications/read-all", null)).StatusCode);
    }

    // ---------- письма: только важное ----------

    [Fact]
    public async Task A_new_request_and_an_accepted_request_are_duplicated_by_email_in_the_users_language()
    {
        var (world, a, b) = await PairAsync(languageB: "ru");

        var requestId = await RequestAsync(world, a, b);
        var letterToB = Assert.Single(_factory.Mail.To(b.User.Email!), m => m.Subject.Contains("новая заявка"));
        Assert.Contains(a.User.Name, letterToB.Body);
        Assert.Contains("/requests", letterToB.Body);

        await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");
        var letterToA = Assert.Single(_factory.Mail.To(a.User.Email!), m => m.Subject.Contains("accepted"));   // у A язык en
        Assert.Contains(b.User.Name, letterToA.Body);
        Assert.Contains("/exchanges/", letterToA.Body);
    }

    [Fact]
    public async Task Other_events_are_shown_in_the_interface_only()
    {
        var (world, a, b) = await PairAsync();
        var requestId = await RequestAsync(world, a, b);
        var lettersA = _factory.Mail.To(a.User.Email!).Count;
        var lettersB = _factory.Mail.To(b.User.Email!).Count;

        await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/decline");
        var reverse = await SendAsync(b, HttpMethod.Post, "/exchange-requests",   // b пишет первым: кулдаун односторонний; это «новая заявка» для a
            new { toUserId = a.Id, toTeachesSkillId = world.S(0), fromTeachesSkillId = world.S(1), durationDays = 30 });
        var again = (await JsonAsync(reverse)).GetProperty("id").GetGuid();
        await SendAsync(a, HttpMethod.Post, $"/exchange-requests/{again}/withdraw");

        Assert.Equal(lettersA + 1, _factory.Mail.To(a.User.Email!).Count);   // только «новая заявка» от b; отказ и отзыв писем не дают
        Assert.Equal(lettersB, _factory.Mail.To(b.User.Email!).Count);
    }

    [Fact]
    public async Task Exchange_events_never_send_email()
    {
        var (world, a, b) = await PairAsync();
        var (_, exchangeId) = await ExchangeAsync(world, a, b);
        var lettersA = _factory.Mail.To(a.User.Email!).Count;
        var lettersB = _factory.Mail.To(b.User.Email!).Count;

        await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/complete");
        await SendAsync(b, HttpMethod.Post, $"/exchanges/{exchangeId}/extend");
        await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/abort", new { reason = "x" });

        Assert.Equal(lettersA, _factory.Mail.To(a.User.Email!).Count);
        Assert.Equal(lettersB, _factory.Mail.To(b.User.Email!).Count);
    }

    [Fact]
    public async Task The_email_job_sends_once_and_skips_unconfirmed_addresses()
    {
        var (world, a, b) = await PairAsync();
        await RequestAsync(world, a, b);
        var id = (await NotificationsAsync(b))[0].GetProperty("id").GetGuid();
        var sent = _factory.Mail.To(b.User.Email!).Count;

        for (var i = 0; i < 3; i++)   // повтор задачи (например, после сбоя) не шлёт второе письмо
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NotificationJobs>().EmailAsync(id);
        }

        Assert.Equal(sent, _factory.Mail.To(b.User.Email!).Count);

        var c = await world.UserAsync(Beginner1, Beginner0);   // почта не подтверждена
        await RequestAsync(world, a, c);
        Assert.Empty(_factory.Mail.To(c.User.Email!));
    }

    [Theory]
    [InlineData(NotificationType.RequestReceived, true)]
    [InlineData(NotificationType.RequestAccepted, true)]
    [InlineData(NotificationType.RequestDeclined, false)]
    [InlineData(NotificationType.RequestExpired, false)]
    [InlineData(NotificationType.ExchangeFinishRequested, false)]
    [InlineData(NotificationType.ExchangeCompleted, false)]
    [InlineData(NotificationType.ExchangeAborted, false)]
    [InlineData(NotificationType.ExtensionProposed, false)]
    [InlineData(NotificationType.ExchangeExtended, false)]
    [InlineData(NotificationType.NewMessages, false)]
    [InlineData(NotificationType.SessionReminder, true)]
    [InlineData(NotificationType.SessionProposed, false)]
    [InlineData(NotificationType.SessionConfirmed, false)]
    [InlineData(NotificationType.SessionNeedsConfirmation, false)]
    public void Only_the_important_types_are_duplicated_by_email(NotificationType type, bool email) =>
        Assert.Equal(email, NotificationPolicy.SendsEmail(type));

    // ---------- хаб ----------

    private HubConnection Connect(string? token, ConcurrentQueue<JsonElement> received, bool tokenInQuery = true)
    {
        var url = new Uri(_client.BaseAddress!, "/hubs/notifications");
        var connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (token is not null && tokenInQuery)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
                else if (token is not null)
                {
                    options.Headers["Authorization"] = "Bearer " + token;
                }
            })
            .Build();
        connection.On<JsonElement>("notification", n => received.Enqueue(n));
        return connection;
    }

    private static async Task<JsonElement> WaitForAsync(ConcurrentQueue<JsonElement> queue, Func<JsonElement, bool> match)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            foreach (var item in queue)
            {
                if (match(item))
                {
                    return item;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Уведомление по хабу не пришло.");
    }

    [Fact]
    public async Task The_hub_delivers_a_new_notification_to_the_connected_recipient_only()
    {
        var (world, a, b) = await PairAsync();
        var forB = new ConcurrentQueue<JsonElement>();
        var forA = new ConcurrentQueue<JsonElement>();
        await using var connectionB = Connect(b.Token, forB);
        await using var connectionA = Connect(a.Token, forA);
        await connectionB.StartAsync();
        await connectionA.StartAsync();

        var requestId = await RequestAsync(world, a, b);

        var pushed = await WaitForAsync(forB, n => n.GetProperty("type").GetString() == "RequestReceived"
            && n.GetProperty("payload").GetProperty("requestId").GetGuid() == requestId);
        Assert.False(pushed.GetProperty("isRead").GetBoolean());
        Assert.Equal(pushed.GetProperty("id").GetGuid(), (await NotificationsAsync(b))[0].GetProperty("id").GetGuid());   // то же уведомление, что и в списке
        await Task.Delay(300);
        Assert.Empty(forA);   // чужие уведомления на соединение отправителя не приходят
    }

    [Fact]
    public async Task The_hub_accepts_the_token_in_a_header_too_and_refuses_anonymous_and_garbage_tokens()
    {
        var (_, a, _) = await PairAsync();
        var queue = new ConcurrentQueue<JsonElement>();

        await using (var viaHeader = Connect(a.Token, queue, tokenInQuery: false))
        {
            await viaHeader.StartAsync();
            Assert.Equal(HubConnectionState.Connected, viaHeader.State);
        }

        await using var anonymous = Connect(null, queue);
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());

        await using var garbage = Connect("not-a-token", queue);
        await Assert.ThrowsAnyAsync<Exception>(() => garbage.StartAsync());
    }
}
