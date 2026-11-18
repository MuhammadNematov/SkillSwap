using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>Чат обмена (раздел 7.1 ТЗ): хаб /hubs/chat, история, шифрование, правка и удаление, «прочитано», режимы только чтение.</summary>
[Collection(ApiCollection.Name)]
public class ChatTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ChatTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    private sealed class Events
    {
        public ConcurrentQueue<(string Name, JsonElement Data)> All { get; } = new();

        public List<JsonElement> Of(string name) => All.Where(e => e.Name == name).Select(e => e.Data).ToList();
    }

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

    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B, Guid ExchangeId)> ExchangeAsync()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
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

    private async Task<HubConnection> ConnectAsync(MatchPerson who, Events events)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/hubs/chat"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(who.Token);
            })
            .Build();
        foreach (var name in new[] { "message", "messageEdited", "messageDeleted", "messagesRead", "typing" })
        {
            var captured = name;
            connection.On<JsonElement>(captured, data => events.All.Enqueue((captured, data)));
        }

        await connection.StartAsync();
        return connection;
    }

    private static async Task<JsonElement> SendMessageAsync(HubConnection connection, Guid exchangeId, string text) =>
        await connection.InvokeAsync<JsonElement>("Send", exchangeId, text);

    private static async Task<JsonElement> WaitEventAsync(Events events, string name, Guid? messageId = null)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            foreach (var data in events.Of(name))
            {
                if (messageId is null || (data.TryGetProperty("id", out var id) && id.GetGuid() == messageId))
                {
                    return data;
                }
            }

            await Task.Delay(40);
        }

        throw new TimeoutException($"Событие чата {name} не пришло.");
    }

    private static async Task ExpectHubErrorAsync(string code, Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<HubException>(call);
        Assert.Contains(code, ex.Message);
    }

    private async Task<JsonElement> HistoryAsync(MatchPerson who, Guid exchangeId, string query = "")
    {
        var response = await SendAsync(who, HttpMethod.Get, $"/exchanges/{exchangeId}/messages{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private Task SetExchangeAsync(Guid exchangeId, ExchangeStatus status, DateTime? closedAt = null) => _factory.QueryAsync(async db =>
    {
        await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, status).SetProperty(e => e.ClosedAt, closedAt));
        return 0;
    });

    private Task AgeMessageAsync(Guid messageId, TimeSpan age) => _factory.QueryAsync(async db =>
    {
        var at = DateTime.UtcNow - age;
        await db.Messages.Where(m => m.Id == messageId).ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, at));
        return 0;
    });

    // ---------- отправка ----------

    [Fact]
    public async Task A_sent_message_reaches_the_partner_and_the_senders_other_tab_but_not_strangers()
    {
        var (world, a, b, exchangeId) = await ExchangeAsync();
        var stranger = await world.UserAsync(Beginner0, Beginner1);
        var forA1 = new Events();
        var forA2 = new Events();
        var forB = new Events();
        var forStranger = new Events();
        await using var a1 = await ConnectAsync(a, forA1);
        await using var a2 = await ConnectAsync(a, forA2);
        await using var bConn = await ConnectAsync(b, forB);
        await using var sConn = await ConnectAsync(stranger, forStranger);

        var sent = await SendMessageAsync(a1, exchangeId, "  Привет! Когда начнём?  ");

        Assert.Equal("Привет! Когда начнём?", sent.GetProperty("text").GetString());   // пробелы по краям убраны
        Assert.Equal(a.Id, sent.GetProperty("senderId").GetGuid());
        Assert.Equal("Text", sent.GetProperty("type").GetString());
        Assert.False(sent.GetProperty("paymentWarning").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("readAt").ValueKind);

        var id = sent.GetProperty("id").GetGuid();
        foreach (var events in new[] { forB, forA2, forA1 })
        {
            var received = await WaitEventAsync(events, "message", id);
            Assert.Equal(id, received.GetProperty("id").GetGuid());
        }

        await Task.Delay(200);
        Assert.Empty(forStranger.All);   // чужой обмен: ничего не приходит
    }

    [Fact]
    public async Task The_text_is_stored_encrypted_and_decrypts_back()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        await using var connection = await ConnectAsync(a, new Events());
        const string secret = "мой-секретный-текст-7391";

        var sent = await SendMessageAsync(connection, exchangeId, secret);

        var stored = await _factory.QueryAsync(db => db.Messages.AsNoTracking().SingleAsync(m => m.Id == sent.GetProperty("id").GetGuid()));
        Assert.DoesNotContain("секретный", stored.EncryptedContent);   // в дампе БД открытого текста нет
        Assert.DoesNotContain("7391", stored.EncryptedContent);
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(secret, scope.ServiceProvider.GetRequiredService<IMessageCipher>().Decrypt(stored.EncryptedContent!));
        Assert.Equal(secret, (await HistoryAsync(a, exchangeId)).GetProperty("items")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Empty_blank_and_too_long_messages_are_refused()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        await using var connection = await ConnectAsync(a, new Events());

        await ExpectHubErrorAsync("message_empty", () => connection.InvokeAsync<JsonElement>("Send", exchangeId, ""));
        await ExpectHubErrorAsync("message_empty", () => connection.InvokeAsync<JsonElement>("Send", exchangeId, "   \n "));
        await ExpectHubErrorAsync("message_empty", () => connection.InvokeAsync<JsonElement>("Send", exchangeId, (string?)null));
        await ExpectHubErrorAsync("message_too_long", () => connection.InvokeAsync<JsonElement>("Send", exchangeId, new string('я', 4001)));
        Assert.Equal(1, (await SendMessageAsync(connection, exchangeId, new string('я', 4000))).GetProperty("text").GetString()!.Length / 4000);   // ровно предел проходит
    }

    [Fact]
    public async Task A_stranger_cannot_send_join_or_read_someone_elses_chat()
    {
        var (world, a, _, exchangeId) = await ExchangeAsync();
        var stranger = await world.UserAsync(Beginner0, Beginner1);
        await using var connection = await ConnectAsync(stranger, new Events());

        await ExpectHubErrorAsync("exchange_not_found", () => connection.InvokeAsync<JsonElement>("Send", exchangeId, "привет"));
        await ExpectHubErrorAsync("exchange_not_found", () => connection.InvokeAsync("Join", exchangeId));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(stranger, HttpMethod.Get, $"/exchanges/{exchangeId}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(stranger, HttpMethod.Post, $"/exchanges/{exchangeId}/messages/read")).StatusCode);
        Assert.Empty((await HistoryAsync(a, exchangeId)).GetProperty("items").EnumerateArray());   // ничего не записалось
    }

    [Fact]
    public async Task The_hub_and_history_refuse_anonymous_callers()
    {
        var (_, _, _, exchangeId) = await ExchangeAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"/api/v1/exchanges/{exchangeId}/messages")).StatusCode);
        var anonymous = new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
    }

    [Fact]
    public async Task An_exchange_created_after_connecting_needs_join_before_its_events_arrive()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var forB = new Events();
        await using var aConn = await ConnectAsync(a, new Events());
        await using var bConn = await ConnectAsync(b, forB);   // подключились до появления обмена
        var exchangeId = await OpenExchangeAsync(world, a, b);

        await aConn.InvokeAsync("Join", exchangeId);
        await SendMessageAsync(aConn, exchangeId, "первое");
        await Task.Delay(300);
        Assert.Empty(forB.Of("message"));   // b ещё не присоединился

        await bConn.InvokeAsync("Join", exchangeId);
        await SendMessageAsync(aConn, exchangeId, "второе");
        var got = await WaitEventAsync(forB, "message");
        Assert.NotNull(got);
        Assert.Single(forB.Of("message"));
    }

    [Fact]
    public async Task After_reconnecting_the_user_is_back_in_the_groups_without_joining()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var forB = new Events();
        await using var aConn = await ConnectAsync(a, new Events());
        await using (var first = await ConnectAsync(b, new Events()))
        {
            await first.StopAsync();
        }

        await using var again = await ConnectAsync(b, forB);   // новое соединение, Join не вызываем
        await SendMessageAsync(aConn, exchangeId, "ты тут?");

        Assert.NotNull(await WaitEventAsync(forB, "message"));
    }

    // ---------- антифрод ----------

    [Theory]
    [InlineData("Мой номер карты 4111 1111 1111 1111, переведи оплату", true)]
    [InlineData("4111-1111-1111-1111", true)]
    [InlineData("Please pay me via PayPal", true)]
    [InlineData("Ба корт пул гузаронед", true)]
    [InlineData("Давай в субботу в 18:00, номер урока 1234 5678 9012 3456", false)]
    [InlineData("Привет! Урок в 18:00, расскажу про Python", false)]
    public async Task Card_numbers_and_payment_requests_get_a_warning_but_are_still_delivered(string text, bool warned)
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var forB = new Events();
        await using var aConn = await ConnectAsync(a, new Events());
        await using var bConn = await ConnectAsync(b, forB);

        var sent = await SendMessageAsync(aConn, exchangeId, text);

        Assert.Equal(warned, sent.GetProperty("paymentWarning").GetBoolean());
        Assert.Equal(text, sent.GetProperty("text").GetString());   // сообщение не блокируется
        var pushed = await WaitEventAsync(forB, "message");
        Assert.NotNull(pushed);
        Assert.Equal(warned, forB.Of("message")[0].GetProperty("paymentWarning").GetBoolean());   // партнёр видит то же предупреждение
        Assert.Equal(warned, (await HistoryAsync(b, exchangeId)).GetProperty("items")[0].GetProperty("paymentWarning").GetBoolean());
    }

    [Fact]
    public async Task An_edit_that_adds_a_card_number_raises_the_warning_and_removing_it_clears_it()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        await using var connection = await ConnectAsync(a, new Events());
        var id = (await SendMessageAsync(connection, exchangeId, "привет")).GetProperty("id").GetGuid();

        var withCard = await connection.InvokeAsync<JsonElement>("Edit", id, "карта 4111 1111 1111 1111");
        Assert.True(withCard.GetProperty("paymentWarning").GetBoolean());

        var clean = await connection.InvokeAsync<JsonElement>("Edit", id, "забудьте, привет");
        Assert.False(clean.GetProperty("paymentWarning").GetBoolean());
    }

    // ---------- история ----------

    [Fact]
    public async Task History_pages_walk_back_with_a_cursor_without_gaps_or_repeats()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var connection = await ConnectAsync(a, new Events());
        var sent = new List<Guid>();
        for (var i = 1; i <= 7; i++)
        {
            sent.Add((await SendMessageAsync(connection, exchangeId, $"сообщение {i}")).GetProperty("id").GetGuid());
            await Task.Delay(5);
        }

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await HistoryAsync(b, exchangeId, "?limit=3" + (cursor is null ? "" : "&before=" + Uri.EscapeDataString(cursor)));
            var ids = page.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("id").GetGuid()).ToList();
            seen.InsertRange(0, ids);   // страницы идут от новых к старым, внутри страницы — по возрастанию
            cursor = page.GetProperty("nextBefore").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextBefore").GetString();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);               // 3 + 3 + 1
        Assert.Equal(sent, seen);             // ровно как отправлено, без пропусков и повторов
    }

    [Fact]
    public async Task Messages_with_the_same_timestamp_are_not_lost_between_pages()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        using var scope = _factory.Services.CreateScope();
        var ids = await _factory.QueryAsync(async db =>
        {
            var cipher = scope.ServiceProvider.GetRequiredService<IMessageCipher>();
            var batch = Enumerable.Range(0, 7).Select(i => Message.CreateText(exchangeId, a.Id, cipher.Encrypt("x" + i), false)).ToList();
            db.Messages.AddRange(batch);
            await db.SaveChangesAsync();   // одна сохранение: у всех одинаковое время
            return batch.Select(m => m.Id).ToList();
        });

        var seen = new HashSet<Guid>();
        string? cursor = null;
        do
        {
            var page = await HistoryAsync(a, exchangeId, "?limit=2" + (cursor is null ? "" : "&before=" + Uri.EscapeDataString(cursor)));
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                Assert.True(seen.Add(item.GetProperty("id").GetGuid()));
            }

            cursor = page.GetProperty("nextBefore").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextBefore").GetString();
        }
        while (cursor is not null);

        Assert.Equal(ids.ToHashSet(), seen);
    }

    [Theory]
    [InlineData("?before=not-a-cursor")]
    [InlineData("?before=AAAA")]
    [InlineData("?limit=0")]
    [InlineData("?limit=101")]
    public async Task A_bad_cursor_or_limit_is_a_400(string query)
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();

        var response = await SendAsync(a, HttpMethod.Get, $"/exchanges/{exchangeId}/messages{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_chat_has_an_empty_history()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();

        var page = await HistoryAsync(a, exchangeId);

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextBefore").ValueKind);
    }

    // ---------- правка и удаление ----------

    [Fact]
    public async Task Editing_keeps_the_previous_text_for_the_moderator_and_notifies_the_partner()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var forB = new Events();
        await using var aConn = await ConnectAsync(a, new Events());
        await using var bConn = await ConnectAsync(b, forB);
        var id = (await SendMessageAsync(aConn, exchangeId, "версия один")).GetProperty("id").GetGuid();

        var edited = await aConn.InvokeAsync<JsonElement>("Edit", id, "версия два");

        Assert.Equal("версия два", edited.GetProperty("text").GetString());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);
        var pushed = await WaitEventAsync(forB, "messageEdited");
        Assert.NotNull(pushed);
        Assert.Equal("версия два", forB.Of("messageEdited")[0].GetProperty("text").GetString());

        var revision = await _factory.QueryAsync(db => db.MessageRevisions.AsNoTracking().SingleAsync(r => r.MessageId == id));
        using var scope = _factory.Services.CreateScope();
        Assert.Equal("версия один", scope.ServiceProvider.GetRequiredService<IMessageCipher>().Decrypt(revision.PreviousEncryptedContent));
    }

    [Fact]
    public async Task Saving_the_same_text_is_not_an_edit()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        await using var connection = await ConnectAsync(a, new Events());
        var id = (await SendMessageAsync(connection, exchangeId, "как было")).GetProperty("id").GetGuid();

        var same = await connection.InvokeAsync<JsonElement>("Edit", id, " как было ");

        Assert.Equal(JsonValueKind.Null, same.GetProperty("editedAt").ValueKind);
        Assert.Equal(0, await _factory.QueryAsync(db => db.MessageRevisions.CountAsync(r => r.MessageId == id)));
    }

    [Fact]
    public async Task Deleting_hides_the_text_from_everyone_but_keeps_it_for_the_moderator()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var forB = new Events();
        await using var aConn = await ConnectAsync(a, new Events());
        await using var bConn = await ConnectAsync(b, forB);
        var id = (await SendMessageAsync(aConn, exchangeId, "это удалят")).GetProperty("id").GetGuid();

        var deleted = await aConn.InvokeAsync<JsonElement>("Delete", id);

        Assert.Equal(id, deleted.GetProperty("messageId").GetGuid());
        Assert.NotNull(await WaitEventAsync(forB, "messageDeleted"));
        foreach (var person in new[] { a, b })
        {
            var item = (await HistoryAsync(person, exchangeId)).GetProperty("items")[0];
            Assert.Equal(JsonValueKind.Null, item.GetProperty("text").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, item.GetProperty("deletedAt").ValueKind);
        }

        var revision = await _factory.QueryAsync(db => db.MessageRevisions.AsNoTracking().SingleAsync(r => r.MessageId == id));
        using var scope = _factory.Services.CreateScope();
        Assert.Equal("это удалят", scope.ServiceProvider.GetRequiredService<IMessageCipher>().Decrypt(revision.PreviousEncryptedContent));
        await ExpectHubErrorAsync("message_deleted", () => aConn.InvokeAsync<JsonElement>("Edit", id, "ещё раз"));
        await ExpectHubErrorAsync("message_deleted", () => aConn.InvokeAsync<JsonElement>("Delete", id));
    }

    [Fact]
    public async Task Only_the_author_edits_or_deletes_and_a_stranger_sees_nothing()
    {
        var (world, a, b, exchangeId) = await ExchangeAsync();
        var stranger = await world.UserAsync(Beginner0, Beginner1);
        await using var aConn = await ConnectAsync(a, new Events());
        await using var bConn = await ConnectAsync(b, new Events());
        await using var sConn = await ConnectAsync(stranger, new Events());
        var id = (await SendMessageAsync(aConn, exchangeId, "моё")).GetProperty("id").GetGuid();

        await ExpectHubErrorAsync("not_message_author", () => bConn.InvokeAsync<JsonElement>("Edit", id, "подмена"));
        await ExpectHubErrorAsync("not_message_author", () => bConn.InvokeAsync<JsonElement>("Delete", id));
        await ExpectHubErrorAsync("message_not_found", () => sConn.InvokeAsync<JsonElement>("Edit", id, "подмена"));
        await ExpectHubErrorAsync("message_not_found", () => sConn.InvokeAsync<JsonElement>("Delete", id));
        await ExpectHubErrorAsync("message_not_found", () => aConn.InvokeAsync<JsonElement>("Edit", Guid.NewGuid(), "x"));

        Assert.Equal("моё", (await HistoryAsync(b, exchangeId)).GetProperty("items")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task After_15_minutes_the_message_can_no_longer_be_changed()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        await using var connection = await ConnectAsync(a, new Events());
        var young = (await SendMessageAsync(connection, exchangeId, "почти поздно")).GetProperty("id").GetGuid();
        var old = (await SendMessageAsync(connection, exchangeId, "уже поздно")).GetProperty("id").GetGuid();
        await AgeMessageAsync(young, TimeSpan.FromMinutes(14));
        await AgeMessageAsync(old, TimeSpan.FromMinutes(16));

        Assert.Equal("правка", (await connection.InvokeAsync<JsonElement>("Edit", young, "правка")).GetProperty("text").GetString());
        await ExpectHubErrorAsync("edit_window_expired", () => connection.InvokeAsync<JsonElement>("Edit", old, "правка"));
        await ExpectHubErrorAsync("edit_window_expired", () => connection.InvokeAsync<JsonElement>("Delete", old));
        Assert.Equal("уже поздно", (await HistoryAsync(a, exchangeId)).GetProperty("items")[0].GetProperty("text").GetString());
    }

    // ---------- прочитано, «печатает», непрочитанные ----------

    [Fact]
    public async Task Reading_marks_the_partners_messages_and_tells_the_sender()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var forA = new Events();
        await using var aConn = await ConnectAsync(a, forA);
        await using var bConn = await ConnectAsync(b, new Events());
        var first = (await SendMessageAsync(aConn, exchangeId, "один")).GetProperty("id").GetGuid();
        var second = (await SendMessageAsync(aConn, exchangeId, "два")).GetProperty("id").GetGuid();
        var third = (await SendMessageAsync(aConn, exchangeId, "три")).GetProperty("id").GetGuid();
        var own = (await SendMessageAsync(bConn, exchangeId, "ответ")).GetProperty("id").GetGuid();

        var receipt = await bConn.InvokeAsync<JsonElement>("Read", exchangeId, second);

        Assert.Equal(2, receipt.GetProperty("marked").GetInt32());   // «один» и «два»; «три» ещё нет
        Assert.NotNull(await WaitEventAsync(forA, "messagesRead"));
        Assert.Equal(b.Id, forA.Of("messagesRead")[0].GetProperty("readerId").GetGuid());
        var items = (await HistoryAsync(a, exchangeId)).GetProperty("items").EnumerateArray().ToList();
        Assert.NotEqual(JsonValueKind.Null, items.Single(i => i.GetProperty("id").GetGuid() == first).GetProperty("readAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, items.Single(i => i.GetProperty("id").GetGuid() == second).GetProperty("readAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, items.Single(i => i.GetProperty("id").GetGuid() == third).GetProperty("readAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, items.Single(i => i.GetProperty("id").GetGuid() == own).GetProperty("readAt").ValueKind);   // свои не отмечаются

        var again = await bConn.InvokeAsync<JsonElement>("Read", exchangeId, second);
        Assert.Equal(0, again.GetProperty("marked").GetInt32());   // повтор безвреден
    }

    [Fact]
    public async Task The_rest_endpoint_marks_everything_read_and_unread_counters_follow()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var aConn = await ConnectAsync(a, new Events());
        await SendMessageAsync(aConn, exchangeId, "раз");
        await SendMessageAsync(aConn, exchangeId, "два");
        var deleted = (await SendMessageAsync(aConn, exchangeId, "три")).GetProperty("id").GetGuid();
        await aConn.InvokeAsync<JsonElement>("Delete", deleted);   // удалённое непрочитанным не считается

        var mine = (await JsonAsync(await SendAsync(b, HttpMethod.Get, "/exchanges"))).GetProperty("items")[0];
        Assert.Equal(2, mine.GetProperty("unreadMessages").GetInt32());
        var theirs = (await JsonAsync(await SendAsync(a, HttpMethod.Get, "/exchanges"))).GetProperty("items")[0];
        Assert.Equal(0, theirs.GetProperty("unreadMessages").GetInt32());   // свои сообщения не в счёт

        var read = await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/exchanges/{exchangeId}/messages/read"));
        Assert.Equal(3, read.GetProperty("marked").GetInt32());   // отмечаются и удалённое тоже: счётчик по нему всё равно не растёт

        var after = await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}"));
        Assert.Equal(0, after.GetProperty("unreadMessages").GetInt32());
        Assert.Equal("ReadWrite", after.GetProperty("chat").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Reading_up_to_a_message_of_another_exchange_is_refused()
    {
        var (world, a, b, exchangeId) = await ExchangeAsync();
        var c = await world.UserAsync(Beginner1, Beginner0);
        var otherExchange = await OpenExchangeAsync(world, a, c);
        await using var connection = await ConnectAsync(a, new Events());
        var foreign = (await SendMessageAsync(connection, otherExchange, "в другом чате")).GetProperty("id").GetGuid();

        var response = await SendAsync(b, HttpMethod.Post, $"/exchanges/{exchangeId}/messages/read", new { upToMessageId = foreign });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Typing_goes_to_the_partner_only_and_is_throttled()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var forA = new Events();
        var forB = new Events();
        await using var aConn = await ConnectAsync(a, forA);
        await using var bConn = await ConnectAsync(b, forB);

        await aConn.InvokeAsync("Typing", exchangeId);
        await aConn.InvokeAsync("Typing", exchangeId);   // слишком часто: второе не пересылается
        await aConn.InvokeAsync("Typing", exchangeId);

        var typing = await WaitEventAsync(forB, "typing");
        Assert.NotNull(typing);
        await Task.Delay(300);
        Assert.Single(forB.Of("typing"));
        Assert.Equal(a.Id, forB.Of("typing")[0].GetProperty("userId").GetGuid());
        Assert.Empty(forA.Of("typing"));   // самому себе не показываем
    }

    // ---------- режимы доступа ----------

    [Theory]
    [InlineData(ExchangeStatus.Frozen, "frozen")]
    [InlineData(ExchangeStatus.Completed, "completed")]
    [InlineData(ExchangeStatus.Aborted, "aborted")]
    public async Task A_chat_that_is_not_active_is_read_only_but_history_and_reading_still_work(ExchangeStatus status, string reason)
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var aConn = await ConnectAsync(a, new Events());
        var id = (await SendMessageAsync(aConn, exchangeId, "до закрытия")).GetProperty("id").GetGuid();
        await SetExchangeAsync(exchangeId, status, status == ExchangeStatus.Frozen ? null : DateTime.UtcNow.AddDays(-1));

        var write = await Assert.ThrowsAsync<HubException>(() => SendMessageAsync(aConn, exchangeId, "после закрытия"));
        Assert.Contains("chat_read_only", write.Message);
        await ExpectHubErrorAsync("chat_read_only", () => aConn.InvokeAsync<JsonElement>("Edit", id, "правка"));
        await ExpectHubErrorAsync("chat_read_only", () => aConn.InvokeAsync<JsonElement>("Delete", id));
        await ExpectHubErrorAsync("chat_read_only", () => aConn.InvokeAsync("Typing", exchangeId));

        Assert.Equal("до закрытия", (await HistoryAsync(b, exchangeId)).GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Equal(1, (await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/exchanges/{exchangeId}/messages/read"))).GetProperty("marked").GetInt32());

        var card = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("chat");
        Assert.Equal("ReadOnly", card.GetProperty("mode").GetString());
        Assert.Equal(reason, card.GetProperty("readOnlyReason").GetString());
    }

    [Fact]
    public async Task The_reason_is_returned_in_the_problem_details_for_rest_clients_too()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        await SetExchangeAsync(exchangeId, ExchangeStatus.Completed, DateTime.UtcNow.AddDays(-2));

        var card = (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("chat");

        Assert.NotEqual(JsonValueKind.Null, card.GetProperty("readableUntil").ValueKind);   // до какого дня можно читать
    }

    [Fact]
    public async Task Thirty_days_after_closing_the_chat_is_no_longer_available_to_participants()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        await using var aConn = await ConnectAsync(a, new Events());
        await SendMessageAsync(aConn, exchangeId, "давно");
        await SetExchangeAsync(exchangeId, ExchangeStatus.Completed, DateTime.UtcNow.AddDays(-31));

        var history = await SendAsync(a, HttpMethod.Get, $"/exchanges/{exchangeId}/messages");
        Assert.Equal(HttpStatusCode.Forbidden, history.StatusCode);
        Assert.Equal("chat_archived", (await JsonAsync(history)).GetProperty("code").GetString());
        await ExpectHubErrorAsync("chat_archived", () => SendMessageAsync(aConn, exchangeId, "ещё"));
        await ExpectHubErrorAsync("chat_archived", () => aConn.InvokeAsync("Join", exchangeId));

        var card = (await JsonAsync(await SendAsync(a, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("chat");
        Assert.Equal("Archived", card.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Just_inside_30_days_the_chat_is_still_readable()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        await using var aConn = await ConnectAsync(a, new Events());
        await SendMessageAsync(aConn, exchangeId, "ещё читается");
        await SetExchangeAsync(exchangeId, ExchangeStatus.Aborted, DateTime.UtcNow.AddDays(-29));

        Assert.Single((await HistoryAsync(a, exchangeId)).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_block_aborts_the_exchange_and_the_chat_turns_read_only_for_both()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await using var aConn = await ConnectAsync(a, new Events());
        await using var bConn = await ConnectAsync(b, new Events());
        await SendMessageAsync(aConn, exchangeId, "последнее");

        await SendAsync(a, HttpMethod.Post, $"/users/{b.Id}/block");

        await ExpectHubErrorAsync("chat_read_only", () => SendMessageAsync(bConn, exchangeId, "ответ"));
        await ExpectHubErrorAsync("chat_read_only", () => SendMessageAsync(aConn, exchangeId, "ещё"));
        Assert.Single((await HistoryAsync(b, exchangeId)).GetProperty("items").EnumerateArray());
    }

    // ---------- лимит частоты ----------

    [Fact]
    public async Task Sending_in_a_flood_is_throttled_with_a_clear_code()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        await using var connection = await ConnectAsync(a, new Events());
        for (var i = 0; i < 20; i++)
        {
            await SendMessageAsync(connection, exchangeId, "м" + i);
        }

        await ExpectHubErrorAsync("chat_rate_limited", () => SendMessageAsync(connection, exchangeId, "лишнее"));
        Assert.Equal(20, (await HistoryAsync(a, exchangeId, "?limit=100")).GetProperty("items").GetArrayLength());
    }
}
