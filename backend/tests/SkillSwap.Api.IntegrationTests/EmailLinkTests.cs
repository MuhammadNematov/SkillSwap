using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Auth;
using SkillSwap.Infrastructure.Auth;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>Ссылки из писем (подтверждение email, сброс пароля): адрес фронтенда, параметры и URL-безопасный токен.</summary>
[Collection(ApiCollection.Name)]
public partial class EmailLinkTests
{
    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public EmailLinkTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    [GeneratedRegex(@"https?://\S+/(confirm-email|reset-password)\?userId=([0-9a-f-]{36})&token=(\S+)")]
    private static partial Regex LinkRegex();

    private (string Page, string UserId, string Token, string Link) LastLink(string email, string page)
    {
        var mail = _factory.Mail.To(email).Last(m => m.Body.Contains(page + "?userId="));
        var match = LinkRegex().Matches(mail.Body).Single(m => m.Groups[1].Value == page);
        return (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Value);
    }

    // ---------- вид ссылки ----------

    [Fact]
    public async Task The_confirmation_link_points_at_the_frontend_with_user_id_and_a_url_safe_token()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();

        var link = LastLink(email, "confirm-email");

        Assert.StartsWith($"{ApiFactory.FrontendUrl}/confirm-email?userId=", link.Link);   // адрес берётся из настроек
        Assert.Matches("^[A-Za-z0-9_-]+$", link.Token);                              // без «+», «/», «=» и процентного кодирования
        Assert.DoesNotContain("%", link.Link);
        Assert.Equal(1, link.Link.Count(c => c == '?'));
        Assert.Equal(1, link.Link.Count(c => c == '&'));                              // ровно два параметра: userId и token
    }

    [Fact]
    public async Task The_reset_link_points_at_the_frontend_with_user_id_and_a_url_safe_token()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        (await _api.ForgotPasswordAsync(email)).EnsureSuccessStatusCode();

        var link = LastLink(email, "reset-password");

        Assert.StartsWith($"{ApiFactory.FrontendUrl}/reset-password?userId=", link.Link);
        Assert.Matches("^[A-Za-z0-9_-]+$", link.Token);
        Assert.DoesNotContain("%", link.Link);
        Assert.Equal(1, link.Link.Count(c => c == '&'));
    }

    // ---------- токен как есть ----------

    [Fact]
    public async Task The_token_from_the_link_works_as_is_for_confirmation_and_for_password_reset()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();
        var confirm = LastLink(email, "confirm-email");

        Assert.Equal(HttpStatusCode.NoContent, (await _api.ConfirmEmailAsync(confirm.UserId, confirm.Token)).StatusCode);   // без перекодирования

        (await _api.ForgotPasswordAsync(email)).EnsureSuccessStatusCode();
        var reset = LastLink(email, "reset-password");
        var reply = await _api.ResetPasswordAsync(reset.UserId, reset.Token, "new-pass-123");
        Assert.True(reply.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, $"got {reply.StatusCode}");
        Assert.Equal(HttpStatusCode.OK, (await _api.LoginAsync(email, "new-pass-123")).StatusCode);
    }

    [Fact]
    public async Task The_old_standard_base64_form_of_the_token_is_still_accepted()
    {
        // Письма, отправленные до перехода на Base64Url, уже лежат у людей: их ссылки должны продолжать работать.
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();
        var confirm = LastLink(email, "confirm-email");
        var standard = UrlSafeToken.Decode(confirm.Token);
        Assert.Matches("^[A-Za-z0-9+/]+=*$", standard);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.ConfirmEmailAsync(confirm.UserId, standard)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("___")]
    [InlineData("%%%")]
    [InlineData("a b c")]
    [InlineData("не-токен")]
    public async Task A_garbage_token_is_a_400_not_a_server_error(string token)
    {
        var email = await _api.RegisterConfirmedUserAsync();
        (await _api.ForgotPasswordAsync(email)).EnsureSuccessStatusCode();
        var reset = LastLink(email, "reset-password");

        var confirmReply = await _api.ConfirmEmailAsync(reset.UserId, token);
        var resetReply = await _api.ResetPasswordAsync(reset.UserId, token, "new-pass-123");

        Assert.Equal(HttpStatusCode.BadRequest, confirmReply.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, resetReply.StatusCode);
    }

    // ---------- адрес фронтенда из настроек ----------

    [Fact]
    public async Task The_frontend_address_comes_from_the_configuration_and_a_trailing_slash_is_harmless()
    {
        using var app = _factory.WithSettings(null, ("Frontend:BaseUrl", "https://app.skillswap.example/"));
        var client = app.CreateClient();
        var email = AuthApi.NewEmail();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, password = AuthApi.Password, name = "Test", dateOfBirth = "2000-01-01", acceptTerms = true, acceptChatModeration = true,
        });
        response.EnsureSuccessStatusCode();

        Assert.StartsWith("https://app.skillswap.example/confirm-email?userId=", LastLink(email, "confirm-email").Link);   // без двойного «//»
    }

    [Fact]
    public void The_shipped_configuration_points_at_the_vite_dev_server()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        Assert.Equal("http://localhost:5173", config["Frontend:BaseUrl"]);   // в разработке; на сервере задаётся Frontend__BaseUrl
        Assert.Equal("http://localhost:5173", new SkillSwap.Application.Options.FrontendOptions().BaseUrl);
    }

    // ---------- проверка адреса при старте ----------

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "SkillSwap";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("http://localhost:5173", "Development")]
    [InlineData("http://localhost:5173", "Testing")]
    [InlineData("https://skillswap.example", "Production")]
    [InlineData("https://app.skillswap.example/", "Production")]
    [InlineData("https://skillswap.example:8443", "Staging")]
    public void Acceptable_addresses_start(string url, string environment) =>
        FrontendUrlStartupCheck.Validate(url, environment is "Development" or "Testing", environment);

    [Theory]
    [InlineData(null, "Development")]
    [InlineData("", "Development")]
    [InlineData("localhost:5173", "Development")]            // без схемы
    [InlineData("/relative", "Development")]
    [InlineData("ftp://skillswap.example", "Development")]
    [InlineData("http://localhost:5173", "Production")]      // в бою localhost недопустим
    [InlineData("http://127.0.0.1:5173", "Production")]
    [InlineData("https://localhost", "Production")]
    [InlineData("http://skillswap.example", "Production")]   // в бою нужен https
    public void Bad_addresses_stop_the_application(string? url, string environment)
    {
        var devLike = environment is "Development" or "Testing";

        Assert.Throws<InvalidOperationException>(() => FrontendUrlStartupCheck.Validate(url, devLike, environment));
    }
}
