using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.RateLimiting;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Api.IntegrationTests.RateLimiting;

/// <summary>
/// Ограничение частоты на настоящем приложении со значениями ПО УМОЛЧАНИЮ (то, что лежит в appsettings.json):
/// почта 3/час на email и 10/час на IP, вход 10/мин на IP, регистрация 5/час на IP, авторизованный 100/мин.
/// Часы приложения заморожены, поэтому Retry-After точен и сравнивается с ожидаемым значением секунда в секунду.
/// Каждый тест «приходит» со своего IP и со своими email, поэтому тесты не мешают друг другу.
/// </summary>
public class RateLimitingTests : IClassFixture<RateLimitedApiFactory>
{
    private const int Hour = 3600;
    private const int Minute = 60;

    private readonly RateLimitedApiFactory _factory;

    public RateLimitingTests(RateLimitedApiFactory factory)
    {
        _factory = factory;
    }

    private AuthApi NewClient() => new(_factory, TestIps.Next());

    private static async Task<List<HttpStatusCode>> StatusesAsync(int count, Func<int, Task<HttpResponseMessage>> request)
    {
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            statuses.Add((await request(i)).StatusCode);
        }

        return statuses;
    }

    // ---------- конфигурация ----------

    [Fact]
    public void The_shipped_configuration_matches_the_specification()
    {
        var o = _factory.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;

        Assert.True(o.Enabled);
        Assert.Equal((3, Hour), (o.MailPerEmail.PermitLimit, o.MailPerEmail.WindowSeconds));
        Assert.Equal((10, Hour), (o.MailPerIp.PermitLimit, o.MailPerIp.WindowSeconds));
        Assert.Equal((10, Minute), (o.LoginPerIp.PermitLimit, o.LoginPerIp.WindowSeconds));
        Assert.Equal((5, Hour), (o.RegisterPerIp.PermitLimit, o.RegisterPerIp.WindowSeconds));
        Assert.Equal((100, Minute), (o.AuthenticatedPerUser.PermitLimit, o.AuthenticatedPerUser.WindowSeconds));
    }

    // ---------- forgot-password и resend-confirmation: 3 в час на email ----------

    [Fact]
    public async Task Forgot_password_allows_three_requests_per_email_per_hour_then_429()
    {
        var api = NewClient();
        var email = await api.RegisterConfirmedUserAsync();
        var mailsBefore = _factory.Mail.To(email).Count;

        var statuses = await StatusesAsync(3, _ => api.ForgotPasswordAsync(email));
        var fourth = await api.ForgotPasswordAsync(email);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.NoContent, s));
        await RateLimitAssert.RateLimitedAsync(fourth, Hour);
        await RateLimitAssert.RateLimitedAsync(await api.ForgotPasswordAsync(email), Hour);   // и дальше тоже

        // Отказанный запрос до обработчика не дошёл: писем ровно три.
        Assert.Equal(mailsBefore + 3, _factory.Mail.To(email).Count);
    }

    [Fact]
    public async Task Resend_confirmation_has_the_same_limit()
    {
        var api = NewClient();
        var email = AuthApi.NewEmail();
        (await api.RegisterAsync(email)).EnsureSuccessStatusCode();

        var statuses = await StatusesAsync(3, _ => api.ResendConfirmationAsync(email));
        var fourth = await api.ResendConfirmationAsync(email);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.NoContent, s));
        await RateLimitAssert.RateLimitedAsync(fourth, Hour);
    }

    [Fact]
    public async Task Both_mail_endpoints_share_one_counter_per_email()
    {
        var api = NewClient();
        var email = AuthApi.NewEmail();
        (await api.RegisterAsync(email)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await api.ForgotPasswordAsync(email)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.ResendConfirmationAsync(email)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.ForgotPasswordAsync(email)).StatusCode);

        // Три письма на адрес уже ушли двумя способами: четвёртое нельзя ни тем, ни другим.
        await RateLimitAssert.RateLimitedAsync(await api.ResendConfirmationAsync(email), Hour);
        await RateLimitAssert.RateLimitedAsync(await api.ForgotPasswordAsync(email), Hour);
    }

    [Fact]
    public async Task Case_and_spaces_in_the_email_do_not_give_a_way_around_the_limit()
    {
        var api = NewClient();
        var local = Guid.NewGuid().ToString("N");

        var first = await api.ForgotPasswordAsync($"{local}@Example.com");
        var second = await api.ForgotPasswordAsync($" {local}@example.com ");   // пробелы: валидатор отклонит, но попытку засчитают
        var third = await api.ForgotPasswordAsync($"{local.ToUpperInvariant()}@EXAMPLE.COM");
        var fourth = await api.ForgotPasswordAsync($"{local}@example.com");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        RateLimitAssert.NotRateLimited(second);
        Assert.Equal(HttpStatusCode.NoContent, third.StatusCode);
        await RateLimitAssert.RateLimitedAsync(fourth, Hour);
    }

    [Fact]
    public async Task One_exhausted_email_does_not_block_other_emails_from_the_same_ip()
    {
        var api = NewClient();
        var blocked = AuthApi.NewEmail();
        await StatusesAsync(4, _ => api.ForgotPasswordAsync(blocked));

        var other = await api.ForgotPasswordAsync(AuthApi.NewEmail());

        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
    }

    [Fact]
    public async Task The_limit_is_the_same_for_a_known_and_an_unknown_email_so_it_reveals_nothing()
    {
        var api = NewClient();
        var known = await api.RegisterConfirmedUserAsync();
        var unknown = AuthApi.NewEmail();

        var knownStatuses = await StatusesAsync(5, _ => api.ForgotPasswordAsync(known));
        var unknownStatuses = await StatusesAsync(5, _ => api.ForgotPasswordAsync(unknown));

        Assert.Equal(knownStatuses, unknownStatuses);
        Assert.Equal(
            [HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            knownStatuses);
    }

    // ---------- forgot-password и resend-confirmation: 10 в час на IP ----------

    [Fact]
    public async Task Mail_endpoints_allow_ten_requests_per_ip_per_hour_even_for_different_emails()
    {
        var api = NewClient();

        var statuses = await StatusesAsync(10, _ => api.ForgotPasswordAsync(AuthApi.NewEmail()));
        var eleventh = await api.ForgotPasswordAsync(AuthApi.NewEmail());   // свежий email: упёрлись именно в IP

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.NoContent, s));
        await RateLimitAssert.RateLimitedAsync(eleventh, Hour);
    }

    [Fact]
    public async Task The_ip_counter_is_shared_by_both_mail_endpoints()
    {
        var api = NewClient();
        await StatusesAsync(5, _ => api.ForgotPasswordAsync(AuthApi.NewEmail()));
        await StatusesAsync(5, _ => api.ResendConfirmationAsync(AuthApi.NewEmail()));

        await RateLimitAssert.RateLimitedAsync(await api.ResendConfirmationAsync(AuthApi.NewEmail()), Hour);
        await RateLimitAssert.RateLimitedAsync(await api.ForgotPasswordAsync(AuthApi.NewEmail()), Hour);
    }

    [Fact]
    public async Task Another_ip_is_not_affected_by_an_exhausted_one()
    {
        var exhausted = NewClient();
        await StatusesAsync(11, _ => exhausted.ForgotPasswordAsync(AuthApi.NewEmail()));

        var other = NewClient();

        Assert.Equal(HttpStatusCode.NoContent, (await other.ForgotPasswordAsync(AuthApi.NewEmail())).StatusCode);
    }

    // ---------- login и login/2fa: 10 в минуту на IP ----------

    [Fact]
    public async Task Login_allows_ten_attempts_per_minute_per_ip_then_429_even_with_the_right_password()
    {
        var api = NewClient();
        var real = await NewClient().RegisterConfirmedUserAsync();   // настоящий пользователь, регистрация с другого IP

        // Неизвестные email, чтобы не упереться в блокировку аккаунта после 5 неверных паролей.
        var statuses = await StatusesAsync(10, _ => api.LoginAsync(AuthApi.NewEmail(), "wrong-pass1"));
        var eleventh = await api.LoginAsync(real, AuthApi.Password);   // пароль верный, но лимит исчерпан

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        await RateLimitAssert.RateLimitedAsync(eleventh, Minute);
        Assert.Null(AuthApi.SetCookieHeader(eleventh));   // обработчик не выполнялся: сеанс не создан
    }

    [Fact]
    public async Task Login_and_login_2fa_share_one_counter_per_ip()
    {
        var api = NewClient();

        await StatusesAsync(6, _ => api.LoginAsync(AuthApi.NewEmail(), "wrong-pass1"));
        var twoFactor = await StatusesAsync(4, _ => api.LoginTwoFactorAsync("garbage", "123456"));

        Assert.All(twoFactor, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        await RateLimitAssert.RateLimitedAsync(await api.LoginAsync(AuthApi.NewEmail(), "x"), Minute);
        await RateLimitAssert.RateLimitedAsync(await api.LoginTwoFactorAsync("garbage", "123456"), Minute);
    }

    [Fact]
    public async Task A_successful_login_counts_too_so_a_busy_ip_is_limited_alike()
    {
        var api = NewClient();
        var email = await NewClient().RegisterConfirmedUserAsync();

        var statuses = await StatusesAsync(10, _ => api.LoginAsync(email, AuthApi.Password));
        var eleventh = await api.LoginAsync(email, AuthApi.Password);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        await RateLimitAssert.RateLimitedAsync(eleventh, Minute);
    }

    [Fact]
    public async Task The_login_limit_is_per_ip()
    {
        var exhausted = NewClient();
        await StatusesAsync(11, _ => exhausted.LoginAsync(AuthApi.NewEmail(), "wrong-pass1"));
        var email = await NewClient().RegisterConfirmedUserAsync();

        var other = await NewClient().LoginAsync(email, AuthApi.Password);

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task One_ipv6_subnet_counts_as_one_client()
    {
        var prefix = TestIps.NextIpv6Prefix();
        var first = new AuthApi(_factory, prefix + "::1");
        var sameSubnet = new AuthApi(_factory, prefix + "::ffff:1");        // тот же /64, другой адрес внутри
        var otherSubnet = new AuthApi(_factory, TestIps.NextIpv6Prefix() + "::1");

        await StatusesAsync(10, _ => first.LoginAsync(AuthApi.NewEmail(), "wrong-pass1"));

        await RateLimitAssert.RateLimitedAsync(await sameSubnet.LoginAsync(AuthApi.NewEmail(), "wrong-pass1"), Minute);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherSubnet.LoginAsync(AuthApi.NewEmail(), "wrong-pass1")).StatusCode);
    }

    // ---------- register: 5 в час на IP ----------

    [Fact]
    public async Task Register_allows_five_per_ip_per_hour_then_429_and_creates_no_account()
    {
        var api = NewClient();

        var created = await StatusesAsync(5, _ => api.RegisterAsync(AuthApi.NewEmail()));
        var email = AuthApi.NewEmail();
        var sixth = await api.RegisterAsync(email);

        Assert.All(created, s => Assert.Equal(HttpStatusCode.Created, s));
        await RateLimitAssert.RateLimitedAsync(sixth, Hour);
        var exists = await _factory.QueryAsync(db => db.Set<User>().AnyAsync(u => u.Email == email));
        Assert.False(exists);   // отказанная регистрация до обработчика не дошла
    }

    [Fact]
    public async Task Invalid_registrations_count_as_attempts_as_well()
    {
        var api = NewClient();
        await StatusesAsync(3, _ => api.RegisterAsync(AuthApi.NewEmail()));
        var invalid = await StatusesAsync(2, _ => api.RegisterAsync("not-an-email"));

        Assert.All(invalid, s => Assert.Equal(HttpStatusCode.BadRequest, s));
        await RateLimitAssert.RateLimitedAsync(await api.RegisterAsync(AuthApi.NewEmail()), Hour);
    }

    [Fact]
    public async Task The_register_limit_is_per_ip()
    {
        var exhausted = NewClient();
        await StatusesAsync(6, _ => exhausted.RegisterAsync(AuthApi.NewEmail()));

        Assert.Equal(HttpStatusCode.Created, (await NewClient().RegisterAsync(AuthApi.NewEmail())).StatusCode);
    }

    // ---------- независимость групп ----------

    [Fact]
    public async Task Exhausting_login_does_not_block_register_or_mail_for_the_same_ip()
    {
        var api = NewClient();
        await StatusesAsync(11, _ => api.LoginAsync(AuthApi.NewEmail(), "wrong-pass1"));

        Assert.Equal(HttpStatusCode.Created, (await api.RegisterAsync(AuthApi.NewEmail())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.ForgotPasswordAsync(AuthApi.NewEmail())).StatusCode);
    }

    // ---------- восстановление: окно скользит ----------

    [Fact]
    public async Task Retry_after_counts_down_and_the_request_goes_through_once_it_has_passed()
    {
        var api = NewClient();
        var email = AuthApi.NewEmail();
        await StatusesAsync(3, _ => api.ForgotPasswordAsync(email));
        await RateLimitAssert.RateLimitedAsync(await api.ForgotPasswordAsync(email), Hour);

        _factory.Clock.Advance(TimeSpan.FromSeconds(1000));
        await RateLimitAssert.RateLimitedAsync(await api.ForgotPasswordAsync(email), Hour - 1000);   // обратный отсчёт

        _factory.Clock.Advance(TimeSpan.FromSeconds(Hour - 1000));
        Assert.Equal(HttpStatusCode.NoContent, (await api.ForgotPasswordAsync(email)).StatusCode);   // окно прошло
    }

    [Fact]
    public async Task Login_attempts_become_possible_again_after_a_minute()
    {
        var api = NewClient();
        await StatusesAsync(10, _ => api.LoginAsync(AuthApi.NewEmail(), "wrong-pass1"));
        await RateLimitAssert.RateLimitedAsync(await api.LoginAsync(AuthApi.NewEmail(), "x"), Minute);

        _factory.Clock.Advance(TimeSpan.FromSeconds(Minute));

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.LoginAsync(AuthApi.NewEmail(), "wrong-pass1")).StatusCode);
    }

    // ---------- авторизованный пользователь: 100 в минуту ----------

    [Fact]
    public async Task An_authenticated_user_gets_one_hundred_requests_per_minute_then_429()
    {
        var api = NewClient();
        var (access, _) = await api.LoginTokensAsync(await api.RegisterConfirmedUserAsync());

        var statuses = await StatusesAsync(100, _ => api.MeAsync(access));
        var next = await api.MeAsync(access);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        await RateLimitAssert.RateLimitedAsync(next, Minute);
    }

    [Fact]
    public async Task The_authenticated_limit_covers_all_endpoints_of_the_user_together()
    {
        var api = NewClient();
        var (access, _) = await api.LoginTokensAsync(await api.RegisterConfirmedUserAsync());

        await StatusesAsync(60, _ => api.MeAsync(access));
        await StatusesAsync(40, _ => api.ListSessionsAsync(access));

        await RateLimitAssert.RateLimitedAsync(await api.MeAsync(access), Minute);
        await RateLimitAssert.RateLimitedAsync(await api.ListSessionsAsync(access), Minute);
    }

    [Fact]
    public async Task The_authenticated_limit_is_per_user_not_per_ip()
    {
        var shared = TestIps.Next();   // два пользователя за одним адресом (офис за NAT)
        var apiA = new AuthApi(_factory, shared);
        var apiB = new AuthApi(_factory, shared);
        var (accessA, _) = await apiA.LoginTokensAsync(await apiA.RegisterConfirmedUserAsync());
        var (accessB, _) = await apiB.LoginTokensAsync(await apiB.RegisterConfirmedUserAsync());

        await StatusesAsync(101, _ => apiA.MeAsync(accessA));

        Assert.Equal(HttpStatusCode.TooManyRequests, (await apiA.MeAsync(accessA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await apiB.MeAsync(accessB)).StatusCode);   // сосед не пострадал
    }

    [Fact]
    public async Task The_authenticated_limit_does_not_apply_to_requests_without_a_token()
    {
        var api = NewClient();
        var (access, refresh) = await api.LoginTokensAsync(await api.RegisterConfirmedUserAsync());
        await StatusesAsync(101, _ => api.MeAsync(access));

        // Тот же IP, но запрос без Authorization: общий лимит пользователя к нему не применяется.
        var anonymous = await api.RefreshAsync(refresh);

        RateLimitAssert.NotRateLimited(anonymous);
    }

    [Fact]
    public async Task The_authenticated_limit_recovers_after_a_minute()
    {
        var api = NewClient();
        var (access, _) = await api.LoginTokensAsync(await api.RegisterConfirmedUserAsync());
        await StatusesAsync(101, _ => api.MeAsync(access));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await api.MeAsync(access)).StatusCode);

        _factory.Clock.Advance(TimeSpan.FromSeconds(Minute));

        Assert.Equal(HttpStatusCode.OK, (await api.MeAsync(access)).StatusCode);
    }

    [Fact]
    public async Task A_garbage_token_is_not_an_authenticated_user_so_it_gets_401_not_429()
    {
        var api = NewClient();

        var statuses = await StatusesAsync(110, _ => api.MeAsync("garbage"));

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
    }

    // ---------- что лимитами не охвачено (по ТЗ) ----------

    [Fact]
    public async Task Anonymous_endpoints_outside_the_listed_groups_are_not_limited()
    {
        // Зафиксировано как есть: по заданию лимитируются почта, вход, регистрация и авторизованный пользователь.
        // refresh, reset-password, confirm-email и 2fa/setup|verify без токена пока не ограничены.
        var api = NewClient();

        var statuses = await StatusesAsync(120, _ => api.RefreshAsync("never-issued"));

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
    }

    // ---------- формат ответа ----------

    [Fact]
    public async Task The_429_is_readable_by_a_browser_because_retry_after_is_exposed_through_cors()
    {
        var api = NewClient();
        await StatusesAsync(10, _ => api.LoginAsync(AuthApi.NewEmail(), "wrong-pass1"));
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = AuthApi.NewEmail(), password = "x" }),
        };
        request.Headers.Add("Origin", "http://localhost:5173");

        var response = await api.Client.SendAsync(request);

        await RateLimitAssert.RateLimitedAsync(response, Minute);
        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("Retry-After", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Fact]
    public async Task Middleware_and_email_rejections_look_exactly_the_same()
    {
        var api = NewClient();
        var email = AuthApi.NewEmail();
        await StatusesAsync(3, _ => api.ForgotPasswordAsync(email));
        var byEmail = await api.ForgotPasswordAsync(email);            // отказал фильтр по email

        var ipOnly = NewClient();
        await StatusesAsync(10, _ => ipOnly.ForgotPasswordAsync(AuthApi.NewEmail()));
        var byIp = await ipOnly.ForgotPasswordAsync(AuthApi.NewEmail());   // отказал middleware по IP

        await RateLimitAssert.RateLimitedAsync(byEmail, Hour);
        await RateLimitAssert.RateLimitedAsync(byIp, Hour);
        Assert.Equal(byEmail.Content.Headers.ContentType, byIp.Content.Headers.ContentType);
    }
}
