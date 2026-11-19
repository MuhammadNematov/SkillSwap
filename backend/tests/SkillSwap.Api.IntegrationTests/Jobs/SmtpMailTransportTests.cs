using Microsoft.Extensions.Options;
using SkillSwap.Application.Options;
using SkillSwap.Infrastructure.Email;

namespace SkillSwap.Api.IntegrationTests.Jobs;

/// <summary>Отправка письма по SMTP (MailKit) настоящим протоколом на настоящий сервер Mailpit.</summary>
public class SmtpMailTransportTests : IClassFixture<MailpitFixture>
{
    private readonly MailpitFixture _mailpit;

    public SmtpMailTransportTests(MailpitFixture mailpit) => _mailpit = mailpit;

    private SmtpMailTransport Transport(Action<SmtpOptions>? configure = null)
    {
        var options = new SmtpOptions
        {
            Host = _mailpit.Host, Port = _mailpit.SmtpPort, Security = SmtpSecurity.None,
            FromAddress = "no-reply@skillswap.test", FromName = "SkillSwap", TimeoutSeconds = 10,
        };
        configure?.Invoke(options);
        return new SmtpMailTransport(Options.Create(options));
    }

    [Fact]
    public async Task A_letter_arrives_with_sender_recipient_subject_and_body()
    {
        await _mailpit.ClearAsync();

        await Transport().SendAsync("alice@example.com", "Подтвердите email", "Здравствуйте!\nСсылка: https://app.test/confirm-email?userId=1&token=abc");

        var mail = Assert.Single(await _mailpit.WaitForAsync(1));
        Assert.Equal("no-reply@skillswap.test", mail.From);
        Assert.Equal("SkillSwap", mail.FromName);
        Assert.Equal(["alice@example.com"], mail.To);
        Assert.Equal("Подтвердите email", mail.Subject);
        Assert.Contains("https://app.test/confirm-email?userId=1&token=abc", mail.Text);
    }

    [Theory]
    [InlineData("Русский: Привет, мир", "Здравствуйте")]
    [InlineData("Тоҷикӣ: Хуш омадед", "Ташаккур барои сабти ном ӯғҳқҷ")]
    [InlineData("Emoji 🙂 ✓", "Тело с эмодзи 🙂 и 日本語")]
    public async Task Non_ascii_subjects_and_bodies_survive_the_trip(string subject, string body)
    {
        await _mailpit.ClearAsync();

        await Transport().SendAsync("bob@example.com", subject, body);

        var mail = Assert.Single(await _mailpit.WaitForAsync(1));
        Assert.Equal(subject, mail.Subject);
        Assert.Contains(body, mail.Text);
    }

    [Fact]
    public async Task A_line_break_in_the_subject_cannot_inject_a_header()
    {
        await _mailpit.ClearAsync();

        await Transport().SendAsync("victim@example.com", "Привет\r\nBcc: attacker@example.com\r\nX-Evil: 1", "тело");

        var mail = Assert.Single(await _mailpit.WaitForAsync(1));
        Assert.Equal(["victim@example.com"], mail.To);                       // получатель один
        var headers = mail.Raw.Split("\r\n\r\n", 2)[0];
        Assert.DoesNotContain("\nBcc:", headers);                           // нового заголовка не появилось
        Assert.DoesNotContain("\nX-Evil:", headers);
    }

    [Fact]
    public async Task Credentials_are_accepted_when_the_server_asks_for_them()
    {
        await _mailpit.ClearAsync();

        // Mailpit без настроенной проверки принимает любые данные: важно, что клиент с логином и паролем отправляет письмо.
        await Transport(o => { o.User = "user"; o.Password = "secret"; }).SendAsync("carol@example.com", "С логином", "тело");

        Assert.Single(await _mailpit.WaitForAsync(1));
    }

    [Fact]
    public async Task An_unreachable_server_is_an_error_so_that_the_job_is_retried()
    {
        var transport = Transport(o => o.Port = 1);   // на порту 1 никто не слушает

        await Assert.ThrowsAnyAsync<Exception>(() => transport.SendAsync("dave@example.com", "тема", "тело"));
    }

    [Fact]
    public async Task A_malformed_recipient_is_an_error()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Transport().SendAsync("not-an-address", "тема", "тело"));
    }
}
