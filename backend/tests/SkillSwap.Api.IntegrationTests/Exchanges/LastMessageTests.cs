using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Chat;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>lastMessage в карточке обмена (GET /exchanges, GET /exchanges/{id}) для списка чатов: id, тип, превью до 100 символов, время, автор.</summary>
[Collection(ApiCollection.Name)]
public class LastMessageTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public LastMessageTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    private async Task<HttpResponseMessage> ApiAsync(MatchPerson who, HttpMethod method, string url, object? body = null)
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
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id || u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var created = await ApiAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var exchangeId = (await JsonAsync(await ApiAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept"))).GetProperty("exchangeId").GetGuid();
        return (world, a, b, exchangeId);
    }

    private HubConnection Hub(MatchPerson who) => new HubConnectionBuilder()
        .WithUrl(new Uri(_client.BaseAddress!, "/hubs/chat"), o =>
        {
            o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            o.AccessTokenProvider = () => Task.FromResult<string?>(who.Token);
        })
        .Build();

    /// <summary>Отправляет текст через хаб (так сообщения создаёт фронтенд) и возвращает id сообщения.</summary>
    private async Task<Guid> SendAsync(MatchPerson who, Guid exchangeId, string text)
    {
        await using var connection = Hub(who);
        await connection.StartAsync();
        var message = await connection.InvokeAsync<JsonElement>("Send", exchangeId, text);
        return message.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> CardAsync(MatchPerson who, Guid exchangeId) =>
        await JsonAsync(await ApiAsync(who, HttpMethod.Get, $"/exchanges/{exchangeId}"));

    private async Task<JsonElement> ListedAsync(MatchPerson who, Guid exchangeId)
    {
        var list = await JsonAsync(await ApiAsync(who, HttpMethod.Get, "/exchanges"));
        return list.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == exchangeId);
    }

    // ---------- есть и нет ----------

    [Fact]
    public async Task A_chat_without_messages_has_no_last_message_in_the_card_and_in_the_list()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();

        Assert.Equal(JsonValueKind.Null, (await CardAsync(a, exchangeId)).GetProperty("lastMessage").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ListedAsync(a, exchangeId)).GetProperty("lastMessage").ValueKind);
    }

    [Fact]
    public async Task A_text_message_shows_its_id_type_preview_time_and_author_to_both_participants()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var id = await SendAsync(a, exchangeId, "Привет, давай начнём в среду");

        foreach (var who in new[] { a, b })
        {
            var last = (await ListedAsync(who, exchangeId)).GetProperty("lastMessage");
            Assert.Equal(id, last.GetProperty("id").GetGuid());
            Assert.Equal("Text", last.GetProperty("type").GetString());
            Assert.Equal("Привет, давай начнём в среду", last.GetProperty("preview").GetString());
            Assert.Equal(a.Id, last.GetProperty("senderId").GetGuid());
            Assert.False(last.GetProperty("isDeleted").GetBoolean());
            Assert.True(last.GetProperty("createdAt").GetDateTime() > DateTime.UtcNow.AddMinutes(-10));
        }

        Assert.Equal(id, (await CardAsync(b, exchangeId)).GetProperty("lastMessage").GetProperty("id").GetGuid());   // и в одиночной карточке
    }

    [Fact]
    public async Task The_newest_message_wins_whoever_wrote_it()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        await SendAsync(a, exchangeId, "первое");
        await SendAsync(b, exchangeId, "второе");
        var last = await SendAsync(a, exchangeId, "третье");

        var card = (await ListedAsync(b, exchangeId)).GetProperty("lastMessage");

        Assert.Equal(last, card.GetProperty("id").GetGuid());
        Assert.Equal("третье", card.GetProperty("preview").GetString());
        Assert.Equal(a.Id, card.GetProperty("senderId").GetGuid());
    }

    [Fact]
    public async Task Each_chat_in_the_list_has_its_own_last_message_and_an_empty_one_stays_empty()
    {
        var (world, a, b, first) = await ExchangeAsync();
        var c = await world.UserAsync(Beginner1, Beginner0);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        var created = await ApiAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = c.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var second = (await JsonAsync(await ApiAsync(c, HttpMethod.Post, $"/exchange-requests/{requestId}/accept"))).GetProperty("exchangeId").GetGuid();
        await SendAsync(a, first, "в первом чате");

        Assert.Equal("в первом чате", (await ListedAsync(a, first)).GetProperty("lastMessage").GetProperty("preview").GetString());
        Assert.Equal(JsonValueKind.Null, (await ListedAsync(a, second)).GetProperty("lastMessage").ValueKind);
        await SendAsync(c, second, "во втором");
        Assert.Equal("во втором", (await ListedAsync(a, second)).GetProperty("lastMessage").GetProperty("preview").GetString());
        Assert.Equal("в первом чате", (await ListedAsync(b, first)).GetProperty("lastMessage").GetProperty("preview").GetString());
    }

    // ---------- превью ----------

    [Fact]
    public async Task A_long_text_is_cut_to_a_hundred_characters_with_an_ellipsis_and_exactly_a_hundred_is_kept()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        await SendAsync(a, exchangeId, new string('а', 100));
        Assert.Equal(new string('а', 100), (await ListedAsync(a, exchangeId)).GetProperty("lastMessage").GetProperty("preview").GetString());

        await SendAsync(a, exchangeId, new string('б', 101));
        var preview = (await ListedAsync(a, exchangeId)).GetProperty("lastMessage").GetProperty("preview").GetString()!;

        Assert.Equal(100, preview.Length);
        Assert.EndsWith("…", preview);
        Assert.Equal(new string('б', 99) + "…", preview);
    }

    [Fact]
    public async Task Line_breaks_become_spaces_in_the_preview_but_the_message_itself_is_untouched()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var id = await SendAsync(a, exchangeId, "  первая строка\n\nвторая\tстрока  ");

        var preview = (await ListedAsync(b, exchangeId)).GetProperty("lastMessage").GetProperty("preview").GetString();

        Assert.Equal("первая строка вторая строка", preview);
        var history = await JsonAsync(await ApiAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}/messages"));
        Assert.Equal("  первая строка\n\nвторая\tстрока  ".Trim(), history.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("id").GetGuid() == id).GetProperty("text").GetString()!.Trim());
    }

    [Fact]
    public async Task A_deleted_message_has_no_preview_but_stays_the_last_one_and_says_so()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var id = await SendAsync(a, exchangeId, "это удалят");
        await using var connection = Hub(a);
        await connection.StartAsync();
        await connection.InvokeAsync<JsonElement>("Delete", id);

        var last = (await ListedAsync(a, exchangeId)).GetProperty("lastMessage");

        Assert.Equal(id, last.GetProperty("id").GetGuid());
        Assert.True(last.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("preview").ValueKind);
        Assert.Equal("Text", last.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(MessageType.Voice)]
    [InlineData(MessageType.File)]
    [InlineData(MessageType.SessionCard)]
    public async Task A_message_without_text_has_a_type_and_no_preview_the_front_end_labels_it_by_type(MessageType type)
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var id = await SendAsync(a, exchangeId, "заменим на вложение");
        await _factory.QueryAsync(async db =>
        {
            await db.Messages.Where(m => m.Id == id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Type, type).SetProperty(m => m.EncryptedContent, (string?)null));
            return 0;
        });

        var last = (await ListedAsync(a, exchangeId)).GetProperty("lastMessage");

        Assert.Equal(type.ToString(), last.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("preview").ValueKind);
    }

    [Fact]
    public async Task The_database_keeps_only_the_encrypted_text_the_preview_is_decrypted_on_the_way_out()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var id = await SendAsync(a, exchangeId, "секретный текст про Python");

        var stored = await _factory.QueryAsync(db => db.Messages.AsNoTracking().Where(m => m.Id == id).Select(m => m.EncryptedContent).SingleAsync());

        Assert.DoesNotContain("Python", stored);
        Assert.Equal("секретный текст про Python", (await ListedAsync(a, exchangeId)).GetProperty("lastMessage").GetProperty("preview").GetString());
    }

    // ---------- сам расчёт превью ----------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   \n\t ", null)]
    [InlineData("коротко", "коротко")]
    [InlineData("  много   пробелов \n и строк ", "много пробелов и строк")]
    public void The_preview_of_short_and_empty_texts(string? text, string? expected) =>
        Assert.Equal(expected, MessagePreview.Of(text));

    [Fact]
    public void The_preview_never_splits_an_emoji_and_never_exceeds_a_hundred_characters()
    {
        var text = new string('x', 98) + "😀😀😀";   // эмодзи — пара суррогатов: граница среза попадает внутрь пары

        var preview = MessagePreview.Of(text)!;

        Assert.True(preview.Length <= MessagePreview.MaxLength, $"{preview.Length}");
        Assert.EndsWith("…", preview);
        for (var i = 0; i < preview.Length; i++)
        {
            if (char.IsHighSurrogate(preview[i]))
            {
                Assert.True(i + 1 < preview.Length && char.IsLowSurrogate(preview[i + 1]), "эмодзи разорван");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(preview[i]), "одинокая половина эмодзи");
            }
        }
    }
}
