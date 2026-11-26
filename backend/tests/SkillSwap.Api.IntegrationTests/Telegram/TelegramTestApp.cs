using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Telegram;

/// <summary>
/// Отдельное приложение и база для тестов Telegram: они переводят часы на часы и дни вперёд, а у общего приложения часы одни на все тесты
/// (дальние переводы ломали бы тесты, сравнивающие их с настоящим временем, которым БД помечает записи).
/// </summary>
public sealed class TelegramApiFactory : ApiFactory;

/// <summary>Приложение с настроенным ботом (на той же тестовой базе) и общие действия тестов Telegram. Бот подставной.</summary>
public sealed class TelegramTestApp : IDisposable
{
    private static long _chat = 700_000_000;

    public const string Secret = "integration-webhook-secret-123456";

    public TelegramTestApp(ApiFactory factory, string? environment = null, params (string Key, string? Value)[] extra)
    {
        Factory = factory;
        App = factory.WithSettings(environment, [.. ApiFactory.TelegramOn, .. extra]);
        Client = App.CreateClient();
    }

    public ApiFactory Factory { get; }

    public WebApplicationFactory<Program> App { get; }

    public HttpClient Client { get; }

    public FakeTelegramClient Bot => Factory.Telegram;

    public InlineBackgroundJobs Jobs => App.Services.GetRequiredService<InlineBackgroundJobs>();

    public static long NextChat() => Interlocked.Increment(ref _chat);

    public DateTime Now => Factory.Clock.GetUtcNow().UtcDateTime;

    /// <summary>Время начала урока без долей секунды: база хранит микросекунды, и сравнение с задачами должно быть точным.</summary>
    public DateTime Slot(int hours) => new(Now.AddHours(hours).Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    public async Task<HttpResponseMessage> SendAsync(MatchPerson? who, HttpMethod method, string url, object? body = null)
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

        return await Client.SendAsync(request);
    }

    public static Task<JsonElement> JsonAsync(HttpResponseMessage response) => response.Content.ReadFromJsonAsync<JsonElement>();

    public async Task<JsonElement> OkAsync(MatchPerson who, HttpMethod method, string url, object? body = null)
    {
        var response = await SendAsync(who, method, url, body);
        Assert.True(response.IsSuccessStatusCode, $"{method} {url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await JsonAsync(response);
    }

    public Task<HttpResponseMessage> WebhookRawAsync(string? secret, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/telegram") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        if (secret is not null)
        {
            request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret);
        }

        return Client.SendAsync(request);
    }

    private static long _updateId = 1000;

    public static string Update(long chatId, string text, string chatType = "private", string language = "ru") => JsonSerializer.Serialize(new
    {
        update_id = Interlocked.Increment(ref _updateId),
        message = new { chat = new { id = chatId, type = chatType }, from = new { language_code = language }, text },
    });

    /// <summary>Бот получает сообщение от чата (через webhook, как в боевой среде).</summary>
    public async Task MessageAsync(long chatId, string text, string chatType = "private") =>
        Assert.Equal(HttpStatusCode.OK, (await WebhookRawAsync(Secret, Update(chatId, text, chatType))).StatusCode);

    /// <summary>Код из ссылки подключения.</summary>
    public async Task<string> CodeAsync(MatchPerson who)
    {
        var url = (await OkAsync(who, HttpMethod.Post, "/me/telegram/link")).GetProperty("url").GetString()!;
        Assert.StartsWith("https://t.me/skillswap_test_bot?start=", url);
        return url.Split("start=")[1];
    }

    public async Task<long> LinkAsync(MatchPerson who)
    {
        var chat = NextChat();
        await MessageAsync(chat, "/start " + await CodeAsync(who));
        return chat;
    }

    public async Task<bool> ConnectedAsync(MatchPerson who) => (await OkAsync(who, HttpMethod.Get, "/me/telegram")).GetProperty("connected").GetBoolean();

    public Task SetLanguageAsync(MatchPerson who, string language) => Factory.QueryAsync(async db =>
    {
        await db.Users.Where(u => u.Id == who.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.UiLanguage, language).SetProperty(u => u.TimeZone, "Asia/Dushanbe"));
        return 0;
    });

    /// <summary>Два пользователя с активным обменом, заведённым через это приложение (уведомления идут через него).</summary>
    public async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B, Guid ExchangeId)> ExchangeAsync()
    {
        var world = await MatchTestWorld.CreateAsync(Factory);
        var a = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner), new MatchTestWorld.Options(Name: "Анвар"));
        var b = await world.UserAsync((1, SkillLevel.Beginner), (0, SkillLevel.Beginner), new MatchTestWorld.Options(Name: "Мехрона"));
        await Factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id || u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true).SetProperty(u => u.UiLanguage, "ru"));
            return 0;
        });
        var created = await OkAsync(a, HttpMethod.Post, "/exchange-requests", new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var accepted = await OkAsync(b, HttpMethod.Post, $"/exchange-requests/{created.GetProperty("id").GetGuid()}/accept");
        return (world, a, b, accepted.GetProperty("exchangeId").GetGuid());
    }

    /// <summary>Урок назначен и подтверждён; возвращает id.</summary>
    public async Task<Guid> LessonAsync(MatchPerson proposer, MatchPerson answerer, Guid exchangeId, DateTime startAt)
    {
        var created = await OkAsync(proposer, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { startAt = startAt.ToString("O"), teacherId = proposer.Id });
        var id = created.GetProperty("id").GetGuid();
        await OkAsync(answerer, HttpMethod.Post, $"/sessions/{id}/confirm");
        return id;
    }

    /// <summary>Переводит часы на момент и выполняет наступившие отложенные задачи.</summary>
    public async Task RunDueAtAsync(DateTime utc)
    {
        Factory.Clock.SetUtcNow(new DateTimeOffset(utc, TimeSpan.Zero));
        await Jobs.RunDueAsync(Factory.Clock.GetUtcNow());
    }

    public void Dispose()
    {
        Client.Dispose();
        App.Dispose();
    }
}
