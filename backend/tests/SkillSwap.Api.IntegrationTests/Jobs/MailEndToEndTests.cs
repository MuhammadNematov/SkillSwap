using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Abstractions;
using SkillSwap.Infrastructure.Email;

namespace SkillSwap.Api.IntegrationTests.Jobs;

/// <summary>
/// Весь путь письма как на боевом сервере: запрос → очередь Hangfire в PostgreSQL → фоновая задача → SMTP → почтовый ящик (Mailpit).
/// Ссылка из полученного письма открывает страницу подтверждения и сброса пароля.
/// </summary>
[Collection(ApiCollection.Name)]
public class MailEndToEndTests : IClassFixture<MailpitFixture>
{
    private readonly ApiFactory _factory;
    private readonly MailpitFixture _mailpit;

    public MailEndToEndTests(ApiFactory factory, MailpitFixture mailpit)
    {
        _factory = factory;
        _mailpit = mailpit;
    }

    private WebApplicationFactory<Program> RealMailApp() =>
        _factory.WithSettings(null,
                ("Hangfire:Enabled", "true"), ("Hangfire:QueuePollIntervalSeconds", "1"),
                ("Smtp:Host", _mailpit.Host), ("Smtp:Port", _mailpit.SmtpPort.ToString()), ("Smtp:Security", "None"),
                ("Smtp:FromAddress", "no-reply@skillswap.test"))
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.RemoveAll<IMailSender>();                        // тесты обычно подставляют перехватчик писем; здесь нужен настоящий
                s.AddScoped<IMailSender, BackgroundMailSender>();
            }));

    private static async Task RegisterAsync(HttpClient client, string email) =>
        (await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, password = AuthApi.Password, name = "Тест", dateOfBirth = "2000-01-01", acceptTerms = true, acceptChatModeration = true,
        })).EnsureSuccessStatusCode();

    private static (string UserId, string Token) LinkIn(MailpitFixture.Mail mail, string page)
    {
        var match = Regex.Match(mail.Text, page + @"\?userId=([0-9a-f-]{36})&token=(\S+)");
        Assert.True(match.Success, "в письме нет ссылки: " + mail.Text);
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    private static string CodeIn(MailpitFixture.Mail mail)
    {
        var match = Regex.Match(mail.Text, @"(?m)^\s*(\d{6})\s*$");   // код стоит отдельной строкой
        Assert.True(match.Success, "в письме нет кода: " + mail.Text);
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task A_registration_letter_travels_through_the_queue_and_smtp_and_its_code_confirms_the_email()
    {
        await _mailpit.ClearAsync();
        using var app = RealMailApp();
        var client = app.CreateClient();
        var email = AuthApi.NewEmail();

        await RegisterAsync(client, email);

        var mail = Assert.Single(await _mailpit.WaitForAsync(1));
        Assert.Equal([email], mail.To);
        Assert.Contains("Subject", mail.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("confirm-email", mail.Text);   // ссылки нет, только код

        var confirm = await client.PostAsJsonAsync("/api/v1/auth/confirm-email/code", new { email, code = CodeIn(mail) });
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
    }

    [Fact]
    public async Task Forgot_password_sends_a_letter_only_for_a_real_account_and_answers_both_the_same_way()
    {
        await _mailpit.ClearAsync();
        using var app = RealMailApp();
        var client = app.CreateClient();
        var email = AuthApi.NewEmail();
        await RegisterAsync(client, email);
        var code = CodeIn(Assert.Single(await _mailpit.WaitForAsync(1)));
        (await client.PostAsJsonAsync("/api/v1/auth/confirm-email/code", new { email, code })).EnsureSuccessStatusCode();
        await _mailpit.ClearAsync();

        var known = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = AuthApi.NewEmail() });

        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());   // ответы неотличимы
        var letters = await _mailpit.WaitForAsync(1);
        await Task.Delay(1500);                                                                              // и второе письмо не придёт
        letters = await _mailpit.WaitForAsync(1, TimeSpan.FromSeconds(1));
        var mail = Assert.Single(letters);
        Assert.Equal([email], mail.To);

        var (resetUserId, resetToken) = LinkIn(mail, "reset-password");
        var reset = await client.PostAsJsonAsync("/api/v1/auth/reset-password", new { userId = resetUserId, token = resetToken, newPassword = "new-pass-123" });
        Assert.True(reset.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, $"got {reset.StatusCode}");
    }

    [Fact]
    public async Task The_request_does_not_wait_for_a_slow_or_broken_mail_server()
    {
        using var app = _factory.WithSettings(null,
                ("Hangfire:Enabled", "true"), ("Hangfire:QueuePollIntervalSeconds", "1"),
                ("Smtp:Host", "localhost"), ("Smtp:Port", "1"), ("Smtp:Security", "None"))   // почтового сервера нет
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.RemoveAll<IMailSender>();
                s.AddScoped<IMailSender, BackgroundMailSender>();
            }));
        var client = app.CreateClient();

        var started = DateTime.UtcNow;
        await RegisterAsync(client, AuthApi.NewEmail());   // регистрация проходит, письмо будет повторяться в фоне

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "запрос ждал почтовый сервер");
    }
}
