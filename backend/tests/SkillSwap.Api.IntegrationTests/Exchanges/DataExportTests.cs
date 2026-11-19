using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Account;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Exchanges;

/// <summary>Выгрузка данных (POST /me/export, раздел 8.1 ТЗ).</summary>
[Collection(ApiCollection.Name)]
public class DataExportTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _storageClient = new();

    public DataExportTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
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

    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B)> PairAsync(string language = "en")
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.EmailConfirmed, true).SetProperty(u => u.UiLanguage, language).SetProperty(u => u.Bio, "Люблю шахматы"));
            await db.Users.Where(u => u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            return 0;
        });
        return (world, a, b);
    }

    private Task<DataExport> LastAsync(Guid userId) =>
        _factory.QueryAsync(db => db.DataExports.AsNoTracking().Where(e => e.UserId == userId).OrderByDescending(e => e.CreatedAt).FirstAsync());

    private string LinkFromMail(string email, string subjectPart)
    {
        var mail = Assert.Single(_factory.Mail.To(email), m => m.Subject.Contains(subjectPart));
        var start = mail.Body.IndexOf("http", StringComparison.Ordinal);
        return mail.Body[start..].Split('\n', ' ')[0].Trim();
    }

    // ---------- запрос и выдача ----------

    [Fact]
    public async Task A_request_builds_the_file_and_the_link_arrives_by_email_for_24_hours()
    {
        var (_, a, _) = await PairAsync();

        var response = await SendAsync(a, HttpMethod.Post, "/me/export");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await JsonAsync(response);
        Assert.False(accepted.TryGetProperty("url", out _));   // ссылки в ответе нет: только на почте
        Assert.Equal("Pending", accepted.GetProperty("status").GetString());

        var status = await JsonAsync(await SendAsync(a, HttpMethod.Get, "/me/export"));
        Assert.Equal("Ready", status.GetProperty("status").GetString());
        Assert.Equal(Now.AddHours(24), status.GetProperty("expiresAt").GetDateTime(), TimeSpan.FromSeconds(5));
        Assert.Equal(accepted.GetProperty("id").GetGuid(), status.GetProperty("id").GetGuid());

        var link = LinkFromMail(a.User.Email!, "your data is ready");
        SignedLinks.AssertLifetime(link, TimeSpan.FromHours(24));   // сутки
        var file = await _storageClient.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("attachment", file.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/json", file.Content.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(await file.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [InlineData("en", "your data is ready")]
    [InlineData("ru", "ваши данные готовы")]
    [InlineData("tg", "маълумоти шумо тайёр аст")]
    public async Task The_email_follows_the_interface_language_and_warns_about_personal_data(string language, string subject)
    {
        var (_, a, _) = await PairAsync(language);

        await SendAsync(a, HttpMethod.Post, "/me/export");

        var mail = Assert.Single(_factory.Mail.To(a.User.Email!), m => m.Subject.Contains(subject));
        Assert.Contains("24", mail.Body);
        Assert.Contains(a.User.Name, mail.Body);
    }

    [Fact]
    public async Task The_file_holds_my_data_my_decrypted_messages_and_nobody_elses()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id || u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            await db.Users.Where(u => u.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Bio, "Люблю шахматы"));
            return 0;
        });
        var created = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30, message = "Привет, давай обменяемся!" });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var accept = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");
        var exchangeId = (await JsonAsync(accept)).GetProperty("exchangeId").GetGuid();
        var session = await SendAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/sessions", new { startAt = Now.AddDays(3).ToString("O"), teacherId = a.Id, topic = "Основы" });
        Assert.Equal(HttpStatusCode.Created, session.StatusCode);
        var cipher = _factory.Services.GetRequiredService<IMessageCipher>();
        await world.ModifyAsync(db =>
        {
            db.Messages.Add(Message.CreateText(exchangeId, a.Id, cipher.Encrypt("мой секретный текст 123"), false));
            db.Messages.Add(Message.CreateText(exchangeId, b.Id, cipher.Encrypt("слова партнёра ZZZ"), false));
            return Task.CompletedTask;
        });
        await SendAsync(a, HttpMethod.Post, $"/users/{(await world.UserAsync(Beginner1, Beginner0)).Id}/block");

        await SendAsync(a, HttpMethod.Post, "/me/export");

        using var json = JsonDocument.Parse(await _storageClient.GetStringAsync(LinkFromMail(a.User.Email!, "your data is ready")));
        var root = json.RootElement;
        var account = root.GetProperty("account");
        Assert.Equal(a.User.Email, account.GetProperty("email").GetString());
        Assert.Equal(a.User.Name, account.GetProperty("name").GetString());
        Assert.Equal("Люблю шахматы", account.GetProperty("bio").GetString());
        Assert.Equal("1995-01-01", account.GetProperty("dateOfBirth").GetString());
        Assert.Equal("en", root.GetProperty("lessonLanguages")[0].GetString());
        Assert.Single(root.GetProperty("skillsTeach").EnumerateArray());
        Assert.Single(root.GetProperty("skillsLearn").EnumerateArray());

        var request = Assert.Single(root.GetProperty("exchangeRequests").EnumerateArray());
        Assert.Equal("outgoing", request.GetProperty("direction").GetString());
        Assert.Equal("Привет, давай обменяемся!", request.GetProperty("message").GetString());
        Assert.Equal(b.User.Name, request.GetProperty("otherUser").GetString());
        var exchange = Assert.Single(root.GetProperty("exchanges").EnumerateArray());
        Assert.Equal(exchangeId, exchange.GetProperty("id").GetGuid());
        Assert.Equal("Active", exchange.GetProperty("status").GetString());
        var lesson = Assert.Single(root.GetProperty("sessions").EnumerateArray());
        Assert.Equal("Основы", lesson.GetProperty("topic").GetString());
        Assert.Single(root.GetProperty("blockedUsers").EnumerateArray());
        Assert.Equal(0, root.GetProperty("reviewsWritten").GetArrayLength());
        Assert.Equal(0, root.GetProperty("reviewsReceived").GetArrayLength());

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Contains(messages, m => m.GetProperty("text").GetString() == "мой секретный текст 123");   // расшифрован
        Assert.All(messages, m => Assert.NotEqual("слова партнёра ZZZ", m.GetProperty("text").GetString()));

        var raw = root.GetRawText();
        Assert.DoesNotContain("слова партнёра ZZZ", raw);          // чужого текста нет
        Assert.DoesNotContain(b.User.Email!, raw);                 // чужих адресов и данных нет
        Assert.DoesNotContain("PasswordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecurityStamp", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task My_deleted_messages_are_still_in_the_export_marked_as_deleted()
    {
        var (world, a, b) = await PairAsync();
        var created = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var exchangeId = (await JsonAsync(await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{(await JsonAsync(created)).GetProperty("id").GetGuid()}/accept"))).GetProperty("exchangeId").GetGuid();
        var cipher = _factory.Services.GetRequiredService<IMessageCipher>();
        await world.ModifyAsync(db =>
        {
            var message = Message.CreateText(exchangeId, a.Id, cipher.Encrypt("удалённое мной"), false);
            db.Messages.Add(message);
            return Task.CompletedTask;
        });
        await _factory.QueryAsync(async db =>
        {
            await db.Messages.Where(m => m.ExchangeId == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(m => m.DeletedAt, Now));
            return 0;
        });

        await SendAsync(a, HttpMethod.Post, "/me/export");

        using var json = JsonDocument.Parse(await _storageClient.GetStringAsync(LinkFromMail(a.User.Email!, "your data is ready")));
        var message = Assert.Single(json.RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("удалённое мной", message.GetProperty("text").GetString());
        Assert.NotEqual(JsonValueKind.Null, message.GetProperty("deletedAt").ValueKind);
    }

    // ---------- не чаще раза в сутки ----------

    [Fact]
    public async Task A_second_request_within_a_day_is_refused_and_after_a_day_it_is_allowed()
    {
        var (_, a, _) = await PairAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(a, HttpMethod.Post, "/me/export")).StatusCode);

        var again = await SendAsync(a, HttpMethod.Post, "/me/export");

        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
        Assert.Equal("export_rate_limited", await CodeAsync(again));
        Assert.Equal(1, await _factory.QueryAsync(db => db.DataExports.CountAsync(e => e.UserId == a.Id)));

        await _factory.QueryAsync(async db =>
        {
            await db.DataExports.Where(e => e.UserId == a.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.CreatedAt, DateTime.UtcNow.AddHours(-25)));
            return 0;
        });
        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(a, HttpMethod.Post, "/me/export")).StatusCode);
        Assert.Equal(2, await _factory.QueryAsync(db => db.DataExports.CountAsync(e => e.UserId == a.Id)));
    }

    [Fact]
    public async Task Simultaneous_requests_create_a_single_export()
    {
        var (_, a, _) = await PairAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SendAsync(a, HttpMethod.Post, "/me/export")));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Accepted);
        Assert.Equal(4, results.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(1, await _factory.QueryAsync(db => db.DataExports.CountAsync(e => e.UserId == a.Id)));
    }

    [Fact]
    public async Task The_limit_is_per_user()
    {
        var (_, a, b) = await PairAsync();
        await SendAsync(a, HttpMethod.Post, "/me/export");

        Assert.Equal(HttpStatusCode.Accepted, (await SendAsync(b, HttpMethod.Post, "/me/export")).StatusCode);
    }

    // ---------- срок ссылки и сбои ----------

    private async Task RunExpireAsync(Guid exportId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DataExportJobs>().ExpireAsync(exportId);
    }

    [Fact]
    public async Task After_24_hours_the_file_is_deleted_and_the_link_stops_working()
    {
        var (_, a, _) = await PairAsync();
        await SendAsync(a, HttpMethod.Post, "/me/export");
        var link = LinkFromMail(a.User.Email!, "your data is ready");
        var export = await LastAsync(a.Id);
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<IBackgroundJobs>();
        Assert.Contains(jobs.Scheduled, j => j.Description.Contains(nameof(DataExportJobs.ExpireAsync)) && Math.Abs((j.RunAt.UtcDateTime - export.ExpiresAt!.Value).TotalSeconds) < 1);

        await RunExpireAsync(export.Id);   // рано
        Assert.Equal(DataExportStatus.Ready, (await LastAsync(a.Id)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _storageClient.GetAsync(link)).StatusCode);

        await _factory.QueryAsync(async db =>
        {
            await db.DataExports.Where(e => e.Id == export.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.ExpiresAt, Now.AddMinutes(-1)));
            return 0;
        });
        await RunExpireAsync(export.Id);
        await RunExpireAsync(export.Id);   // повтор безвреден

        Assert.Equal(DataExportStatus.Expired, (await LastAsync(a.Id)).Status);
        Assert.NotEqual(HttpStatusCode.OK, (await _storageClient.GetAsync(link)).StatusCode);   // файла больше нет
        Assert.Equal("Expired", (await JsonAsync(await SendAsync(a, HttpMethod.Get, "/me/export"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task When_the_storage_fails_the_export_is_marked_failed_and_can_be_requested_again()
    {
        var (_, a, _) = await PairAsync();
        using var down = _factory.WithSettings("Testing", ("Storage:Endpoint", "http://127.0.0.1:1"), ("Storage:TimeoutSeconds", "2"), ("Storage:MaxRetries", "0"));

        var first = await SendAsync(a, HttpMethod.Post, "/me/export", client: down.CreateClient());

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);   // запрос принят, сбой в фоновой задаче
        Assert.Equal(DataExportStatus.Failed, (await LastAsync(a.Id)).Status);
        Assert.Empty(_factory.Mail.To(a.User.Email!).Where(m => m.Subject.Contains("data is ready")));   // письма со ссылкой нет

        var retry = await SendAsync(a, HttpMethod.Post, "/me/export");   // неудача не блокирует повтор
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(DataExportStatus.Ready, (await LastAsync(a.Id)).Status);
    }

    [Fact]
    public async Task Running_the_build_twice_does_not_send_two_letters()
    {
        var (_, a, _) = await PairAsync();
        await SendAsync(a, HttpMethod.Post, "/me/export");
        var export = await LastAsync(a.Id);

        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DataExportJobs>().BuildAsync(export.Id);   // повтор после сбоя

        Assert.Single(_factory.Mail.To(a.User.Email!), m => m.Subject.Contains("data is ready"));
    }

    // ---------- доступ ----------

    [Fact]
    public async Task Status_is_not_found_before_the_first_request_and_anonymous_staff_and_deleted_accounts_are_refused()
    {
        var (world, a, _) = await PairAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(a, HttpMethod.Get, "/me/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Post, "/me/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, "/me/export")).StatusCode);

        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Moderator);
        await world.ModifyAsync(db => { db.Users.Add(staff); return Task.CompletedTask; });
        var staffPerson = new MatchPerson(staff, TestTokens.ForUser(_factory, staff));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(staffPerson, HttpMethod.Post, "/me/export")).StatusCode);

        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Deleted));
            return 0;
        });
        var deleted = await SendAsync(a, HttpMethod.Post, "/me/export");
        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
        Assert.Equal("account_not_active", await CodeAsync(deleted));
    }

    [Fact]
    public async Task One_user_never_sees_the_status_of_anothers_export()
    {
        var (_, a, b) = await PairAsync();
        await SendAsync(a, HttpMethod.Post, "/me/export");

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(b, HttpMethod.Get, "/me/export")).StatusCode);
    }
}
