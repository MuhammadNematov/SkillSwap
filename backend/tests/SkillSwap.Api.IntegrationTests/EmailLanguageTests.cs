using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Auth;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>Письма на трёх языках интерфейса (tg, ru, en) и маршруты фронтенда в ссылках из настроек.</summary>
[Collection(ApiCollection.Name)]
public class EmailLanguageTests
{
    private const string NewPassword = "brand-new-pass-9";
    private static readonly char[] TajikLetters = ['ӣ', 'қ', 'ӯ', 'ҳ', 'ҷ', 'ғ'];

    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public EmailLanguageTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    private Task SetLanguageAsync(string email, string language) => _factory.QueryAsync(async db =>
    {
        await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(s => s.SetProperty(u => u.UiLanguage, language));
        return 0;
    });

    private static bool IsCyrillic(string text) => text.Any(c => c is >= 'А' and <= 'я');

    // ---------- письма аутентификации ----------

    [Theory]
    [InlineData("tg", "тасдиқ")]
    [InlineData("ru", "код подтверждения")]
    [InlineData("en", "confirmation code")]
    public async Task The_confirmation_email_follows_the_interface_language(string language, string expected)
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email, acceptLanguage: language)).EnsureSuccessStatusCode();

        var mail = _factory.Mail.To(email).Last();

        Assert.Contains(expected, mail.Subject);
        Assert.Matches(@"(?m)^\s*\d{6}\s*$", mail.Body);   // код на любом языке; ссылки нет
        Assert.DoesNotContain("confirm-email", mail.Body);
        if (language == "en")
        {
            Assert.False(IsCyrillic(mail.Subject + mail.Body));
        }
    }

    [Theory]
    [InlineData("tg", "барқарор", "1 соат")]
    [InlineData("ru", "сброс пароля", "1 час")]
    [InlineData("en", "password reset", "1 hour")]
    public async Task The_reset_and_changed_emails_follow_the_interface_language(string language, string resetSubject, string hour)
    {
        var email = await _api.RegisterConfirmedUserAsync();
        await SetLanguageAsync(email, language);

        (await _api.ForgotPasswordAsync(email)).EnsureSuccessStatusCode();
        var reset = _factory.Mail.To(email).Last();
        Assert.Contains(resetSubject, reset.Subject);
        Assert.Contains(hour, reset.Body);

        var (userId, token) = _factory.Mail.LastResetLink(email);
        (await _api.ResetPasswordAsync(userId, token, NewPassword)).EnsureSuccessStatusCode();
        var changed = _factory.Mail.To(email).Last();
        Assert.Contains(language switch { "tg" => "тағйир", "ru" => "изменён", _ => "was changed" }, changed.Subject);
        Assert.DoesNotContain(NewPassword, changed.Body);
    }

    [Fact]
    public async Task An_unknown_language_falls_back_to_english()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        await SetLanguageAsync(email, "fr");

        (await _api.ForgotPasswordAsync(email)).EnsureSuccessStatusCode();

        Assert.Contains("password reset", _factory.Mail.To(email).Last().Subject);
    }

    [Fact]
    public async Task Tajik_emails_really_are_tajik_not_a_copy_of_the_russian_text()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        await SetLanguageAsync(email, "tg");
        (await _api.ForgotPasswordAsync(email)).EnsureSuccessStatusCode();

        var mail = _factory.Mail.To(email).Last();

        Assert.Contains(TajikLetters, c => (mail.Subject + mail.Body).Contains(c));   // таджикские буквы: не русский и не английский текст
        Assert.DoesNotContain("пароль", mail.Body);                                    // русское слово
    }

    // ---------- письма уведомлений ----------

    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B)> PairAsync(string languageB, string languageA = "en")
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        var b = await world.UserAsync((1, SkillLevel.Beginner), (0, SkillLevel.Beginner));
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == a.Id || u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
            await db.Users.Where(u => u.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.UiLanguage, languageA));
            await db.Users.Where(u => u.Id == b.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.UiLanguage, languageB));
            return 0;
        });
        return (world, a, b);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, MatchPerson who, string url, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1" + url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", who.Token);
        if (body is not null)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("tg", "дархости нави", "дархости шумо қабул шуд")]
    [InlineData("ru", "новая заявка", "заявка принята")]
    [InlineData("en", "new exchange request", "your request was accepted")]
    public async Task Notification_emails_follow_the_interface_language(string language, string received, string accepted)
    {
        var (world, a, b) = await PairAsync(languageB: language, languageA: language);
        var client = _factory.CreateClient();

        var created = await PostAsync(client, a, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        await PostAsync(client, b, $"/exchange-requests/{requestId}/accept");

        Assert.Single(_factory.Mail.To(b.User.Email!), m => m.Subject.Contains(received));
        Assert.Single(_factory.Mail.To(a.User.Email!), m => m.Subject.Contains(accepted));
    }

    // ---------- маршруты фронтенда в настройках ----------

    [Fact]
    public void Defaults_keep_the_agreed_routes()
    {
        var options = new FrontendOptions { BaseUrl = "https://app.example/" };
        var id = Guid.NewGuid();

        Assert.Equal("https://app.example/requests", options.RequestsUrl());
        Assert.Equal($"https://app.example/exchanges/{id}", options.ExchangeUrl(id));
    }

    [Theory]
    [InlineData("https://app.example", "/inbox", "/swaps/{id}/chat", "https://app.example/inbox", "https://app.example/swaps/{0}/chat")]
    [InlineData("https://app.example/", "inbox", "swaps/{id}", "https://app.example/inbox", "https://app.example/swaps/{0}")]
    [InlineData("https://app.example/app", "/a", "/x/{id}?tab=chat", "https://app.example/app/a", "https://app.example/app/x/{0}?tab=chat")]
    public void Routes_come_from_settings(string baseUrl, string requests, string exchange, string expectedRequests, string expectedExchange)
    {
        var options = new FrontendOptions { BaseUrl = baseUrl, RequestsPath = requests, ExchangePath = exchange };
        var id = Guid.NewGuid();

        Assert.Equal(expectedRequests, options.RequestsUrl());
        Assert.Equal(string.Format(expectedExchange, id), options.ExchangeUrl(id));
    }

    [Theory]
    [InlineData("", "/exchanges/{id}")]
    [InlineData("requests", "/exchanges/{id}")]
    [InlineData("/requests", "")]
    [InlineData("/requests", "/exchanges")]
    [InlineData("/requests", "exchanges/{id}")]
    public void Broken_routes_stop_the_application_at_startup(string requests, string exchange)
    {
        var options = new FrontendOptions { RequestsPath = requests, ExchangePath = exchange };

        Assert.Throws<InvalidOperationException>(() => FrontendUrlStartupCheck.ValidatePaths(options));
    }

    [Fact]
    public void Good_routes_pass_the_startup_check() => FrontendUrlStartupCheck.ValidatePaths(new FrontendOptions());

    [Fact]
    public async Task The_links_in_notification_emails_use_the_configured_routes()
    {
        var (world, a, b) = await PairAsync("en");
        using var custom = _factory.WithSettings(null,
            ("Frontend:BaseUrl", "https://front.example"), ("Frontend:RequestsPath", "/inbox"), ("Frontend:ExchangePath", "/swaps/{id}/chat"));
        var client = custom.CreateClient();

        var created = await PostAsync(client, a, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        var accept = await PostAsync(client, b, $"/exchange-requests/{requestId}/accept");
        var exchangeId = (await accept.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("exchangeId").GetGuid();

        Assert.Contains("https://front.example/inbox", _factory.Mail.To(b.User.Email!).Last().Body);
        Assert.Contains($"https://front.example/swaps/{exchangeId}/chat", _factory.Mail.To(a.User.Email!).Last().Body);
    }
}
