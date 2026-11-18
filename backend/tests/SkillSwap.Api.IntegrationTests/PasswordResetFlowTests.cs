using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Auth;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>
/// Сброс пароля (FR-4) на настоящей БД и настоящем Identity: токен на 1 час и одноразовый,
/// одинаковый ответ для известного и неизвестного email, после сброса отзываются все refresh-токены.
/// </summary>
[Collection(ApiCollection.Name)]
public class PasswordResetFlowTests
{
    private const string NewPassword = "brand-new-pass-9";

    private readonly ApiFactory _factory;
    private readonly AuthApi _api;

    public PasswordResetFlowTests(ApiFactory factory)
    {
        _factory = factory;
        _api = new AuthApi(factory);
    }

    private Task<User> FindUserAsync(string email) =>
        _factory.QueryAsync(db => db.Set<User>().SingleAsync(u => u.Email == email));

    /// <summary>Запрашивает сброс и достаёт ссылку из перехваченного письма.</summary>
    private async Task<(string UserId, string Token)> RequestResetAsync(string email)
    {
        (await _api.ForgotPasswordAsync(email)).EnsureSuccessStatusCode();
        return _factory.Mail.LastResetLink(email);
    }

    // ---------- запрос сброса ----------

    [Fact]
    public async Task Forgot_password_sends_a_reset_link_for_a_confirmed_account()
    {
        var email = await _api.RegisterConfirmedUserAsync();

        var response = await _api.ForgotPasswordAsync(email);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var (userId, token) = _factory.Mail.LastResetLink(email);
        Assert.Equal((await FindUserAsync(email)).Id.ToString(), userId);
        Assert.False(string.IsNullOrEmpty(token));
        var mail = _factory.Mail.To(email).Last();
        Assert.Contains("password reset", mail.Subject);
        Assert.Contains("1 hour", mail.Body);
    }

    [Fact]
    public async Task The_reset_mail_follows_the_language_of_the_account()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email, acceptLanguage: "ru")).EnsureSuccessStatusCode();
        var (userId, token) = _factory.Mail.LastConfirmationLink(email);
        (await _api.ConfirmEmailAsync(userId, token)).EnsureSuccessStatusCode();

        await _api.ForgotPasswordAsync(email);

        Assert.Contains("сброс пароля", _factory.Mail.To(email).Last().Subject);
    }

    [Fact]
    public async Task The_answer_is_identical_for_a_registered_and_an_unknown_email()
    {
        var known = await _api.RegisterConfirmedUserAsync();
        var unknown = AuthApi.NewEmail();

        var a = await _api.ForgotPasswordAsync(known);
        var b = await _api.ForgotPasswordAsync(unknown);

        Assert.Equal(a.StatusCode, b.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, a.StatusCode);
        Assert.Equal(await a.Content.ReadAsStringAsync(), await b.Content.ReadAsStringAsync());
        Assert.Equal(a.Content.Headers.ContentLength, b.Content.Headers.ContentLength);
        // Набор заголовков тоже одинаков (Date отличается по природе).
        static string[] Headers(HttpResponseMessage r) => r.Headers.Select(h => h.Key)
            .Concat(r.Content.Headers.Select(h => h.Key)).Where(h => h != "Date").OrderBy(h => h).ToArray();
        Assert.Equal(Headers(a), Headers(b));

        Assert.Single(_factory.Mail.To(known), m => m.Body.Contains("reset-password"));
        Assert.Empty(_factory.Mail.To(unknown));   // неизвестному ничего не отправлено
    }

    [Fact]
    public async Task No_mail_goes_to_an_unconfirmed_account_but_the_answer_is_still_the_same()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();   // email не подтверждён
        var mailsBefore = _factory.Mail.To(email).Count;

        var response = await _api.ForgotPasswordAsync(email);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(mailsBefore, _factory.Mail.To(email).Count);
    }

    [Fact]
    public async Task No_mail_goes_to_a_blocked_account_but_the_answer_is_still_the_same()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        await _factory.QueryAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Users\" SET \"Status\" = 'Blocked' WHERE \"Email\" = {email}"));

        var response = await _api.ForgotPasswordAsync(email);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(_factory.Mail.To(email), m => m.Body.Contains("reset-password"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    public async Task Forgot_password_with_a_malformed_email_gives_400(string email)
    {
        var response = await _api.ForgotPasswordAsync(email);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await AuthApi.ReadCodeAsync(response));
    }

    // ---------- сам сброс ----------

    [Fact]
    public async Task Reset_sets_the_new_password_and_the_old_one_stops_working()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (userId, token) = await RequestResetAsync(email);

        var reset = await _api.ResetPasswordAsync(userId, token, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.LoginAsync(email, AuthApi.Password)).StatusCode);
        var login = await _api.LoginAsync(email, NewPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.NotNull(AuthApi.RefreshTokenFrom(login));
    }

    [Fact]
    public async Task The_reset_token_works_only_once()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (userId, token) = await RequestResetAsync(email);
        (await _api.ResetPasswordAsync(userId, token, NewPassword)).EnsureSuccessStatusCode();

        var second = await _api.ResetPasswordAsync(userId, token, "another-pass-7");

        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("invalid_reset_token", await AuthApi.ReadCodeAsync(second));
        Assert.Equal(HttpStatusCode.OK, (await _api.LoginAsync(email, NewPassword)).StatusCode);   // пароль остался первым новым
    }

    [Fact]
    public async Task A_newer_reset_link_does_not_break_the_older_one_until_the_password_changes_but_any_use_burns_all()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (userId, first) = await RequestResetAsync(email);
        var (_, second) = await RequestResetAsync(email);

        (await _api.ResetPasswordAsync(userId, second, NewPassword)).EnsureSuccessStatusCode();

        // Смена пароля меняет SecurityStamp: все ранее выданные ссылки сгорают.
        var older = await _api.ResetPasswordAsync(userId, first, "yet-another-1");
        Assert.Equal(HttpStatusCode.BadRequest, older.StatusCode);
        Assert.Equal("invalid_reset_token", await AuthApi.ReadCodeAsync(older));
    }

    [Fact]
    public async Task Reset_revokes_every_refresh_token_of_the_user()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var laptop = await _api.LoginTokensAsync(email);
        var phone = await _api.LoginTokensAsync(email);
        var userId = (await FindUserAsync(email)).Id;
        var (id, token) = await RequestResetAsync(email);

        (await _api.ResetPasswordAsync(id, token, NewPassword)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RefreshAsync(laptop.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.RefreshAsync(phone.RefreshToken)).StatusCode);
        var active = await _factory.QueryAsync(db => db.Set<RefreshToken>().CountAsync(t => t.UserId == userId && t.RevokedAt == null));
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task Reset_leaves_other_users_sessions_alone()
    {
        var emailA = await _api.RegisterConfirmedUserAsync();
        var emailB = await _api.RegisterConfirmedUserAsync();
        var (_, refreshB) = await _api.LoginTokensAsync(emailB);
        var (idA, tokenA) = await RequestResetAsync(emailA);

        (await _api.ResetPasswordAsync(idA, tokenA, NewPassword)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _api.RefreshAsync(refreshB)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_session_refresh_after_reset_is_a_plain_401_and_not_a_reuse_alarm()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (_, refresh) = await _api.LoginTokensAsync(email);
        var (id, token) = await RequestResetAsync(email);
        (await _api.ResetPasswordAsync(id, token, NewPassword)).EnsureSuccessStatusCode();

        var response = await _api.RefreshAsync(refresh);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_refresh_token", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task Reset_clears_a_lockout_caused_by_wrong_passwords()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        for (var i = 0; i < 5; i++)
        {
            await _api.LoginAsync(email, "wrong-pass1");
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await _api.LoginAsync(email, AuthApi.Password)).StatusCode);
        var (id, token) = await RequestResetAsync(email);
        (await _api.ResetPasswordAsync(id, token, NewPassword)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _api.LoginAsync(email, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task Reset_does_not_remove_2FA_so_the_new_password_still_needs_a_code()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (access, _) = await _api.LoginTokensAsync(email);
        await _api.EnableTwoFactorAsync(access);
        var (id, token) = await RequestResetAsync(email);

        (await _api.ResetPasswordAsync(id, token, NewPassword)).EnsureSuccessStatusCode();

        var login = await _api.LoginAsync(email, NewPassword);
        Assert.True(await AuthApi.ReadFlagAsync(login, "requires2fa"));   // пароль есть, а доступа ещё нет
        Assert.Null(AuthApi.SetCookieHeader(login));
    }

    [Fact]
    public async Task Reset_notifies_the_owner_by_email()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (id, token) = await RequestResetAsync(email);

        (await _api.ResetPasswordAsync(id, token, NewPassword)).EnsureSuccessStatusCode();

        var last = _factory.Mail.To(email).Last();
        Assert.Contains("password was changed", last.Subject);
        Assert.DoesNotContain(NewPassword, last.Body);   // сам пароль в письмо не попадает
    }

    // ---------- плохие запросы ----------

    [Fact]
    public async Task A_wrong_token_and_an_unknown_user_give_the_same_400()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var userId = (await FindUserAsync(email)).Id.ToString();

        var wrongToken = await _api.ResetPasswordAsync(userId, "not-a-real-token", NewPassword);
        var unknownUser = await _api.ResetPasswordAsync(Guid.NewGuid().ToString(), "not-a-real-token", NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, wrongToken.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownUser.StatusCode);
        Assert.Equal("invalid_reset_token", await AuthApi.ReadCodeAsync(wrongToken));
        Assert.Equal("invalid_reset_token", await AuthApi.ReadCodeAsync(unknownUser));
        Assert.Equal(HttpStatusCode.OK, (await _api.LoginAsync(email, AuthApi.Password)).StatusCode);   // пароль не тронут
    }

    [Fact]
    public async Task A_token_of_one_user_does_not_reset_another_users_password()
    {
        var emailA = await _api.RegisterConfirmedUserAsync();
        var emailB = await _api.RegisterConfirmedUserAsync();
        var (_, tokenA) = await RequestResetAsync(emailA);
        var idB = (await FindUserAsync(emailB)).Id.ToString();

        var response = await _api.ResetPasswordAsync(idB, tokenA, NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.LoginAsync(emailB, AuthApi.Password)).StatusCode);
    }

    [Fact]
    public async Task A_weak_new_password_gives_400_with_codes_and_does_not_spend_the_link()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (id, token) = await RequestResetAsync(email);

        var weak = await _api.ResetPasswordAsync(id, token, "short");

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var json = await weak.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("validation_failed", json.GetProperty("code").GetString());
        var codes = json.GetProperty("errors").GetProperty("newPassword").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("password_too_short", codes);
        Assert.Contains("password_needs_digit", codes);

        // Та же ссылка ещё работает: слабый пароль её не «сжёг».
        Assert.Equal(HttpStatusCode.NoContent, (await _api.ResetPasswordAsync(id, token, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task The_email_confirmation_token_cannot_be_used_to_reset_a_password()
    {
        var email = AuthApi.NewEmail();
        (await _api.RegisterAsync(email)).EnsureSuccessStatusCode();
        var (userId, confirmationToken) = _factory.Mail.LastConfirmationLink(email);
        (await _api.ConfirmEmailAsync(userId, confirmationToken)).EnsureSuccessStatusCode();

        var response = await _api.ResetPasswordAsync(userId, confirmationToken, NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_reset_token", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task The_reset_token_cannot_be_used_to_confirm_an_email()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (userId, resetToken) = await RequestResetAsync(email);

        var response = await _api.ConfirmEmailAsync(userId, resetToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_confirmation_token", await AuthApi.ReadCodeAsync(response));
    }

    [Fact]
    public async Task Two_parallel_resets_with_the_same_link_let_only_one_through()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (id, token) = await RequestResetAsync(email);

        var results = await Task.WhenAll(
            _api.ResetPasswordAsync(id, token, "first-new-pass-1"),
            _api.ResetPasswordAsync(id, token, "second-new-pass-2"));

        var statuses = string.Join(", ", results.Select(r => (int)r.StatusCode));
        Assert.True(results.Count(r => r.StatusCode == HttpStatusCode.NoContent) == 1, $"Статусы: {statuses}");
        // Проигравший получает 409 (параллельное изменение) или 400 (ссылка уже сгорела), но не 5xx.
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.NoContent),
            r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.BadRequest }));
    }

    // ---------- срок жизни токена: 1 час ----------

    [Fact]
    public void The_reset_token_provider_is_configured_for_one_hour_while_confirmation_keeps_its_own_lifespan()
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        var reset = services.GetRequiredService<IOptions<PasswordResetTokenProviderOptions>>().Value;
        var confirmation = services.GetRequiredService<IOptions<DataProtectionTokenProviderOptions>>().Value;
        var identity = services.GetRequiredService<UserManager<User>>().Options.Tokens;

        Assert.Equal(TimeSpan.FromHours(1), reset.TokenLifespan);                       // FR-4
        Assert.Equal(TimeSpan.FromDays(1), confirmation.TokenLifespan);                 // ссылка подтверждения email не изменилась
        Assert.Equal(PasswordResetTokenProvider.ProviderName, identity.PasswordResetTokenProvider);
    }

    [Fact]
    public async Task A_real_reset_token_is_valid_only_for_the_reset_provider()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        var (_, linkToken) = await RequestResetAsync(email);
        var token = SkillSwap.Application.Auth.UrlSafeToken.Decode(linkToken);   // в ссылке токен в виде Base64Url, Identity ждёт обычный Base64
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await manager.FindByEmailAsync(email);

        Assert.True(await manager.VerifyUserTokenAsync(user!, PasswordResetTokenProvider.ProviderName, UserManager<User>.ResetPasswordTokenPurpose, token));
        Assert.False(await manager.VerifyUserTokenAsync(user!, TokenOptions.DefaultProvider, UserManager<User>.ResetPasswordTokenPurpose, token));
    }

    [Fact]
    public async Task The_provider_rejects_a_token_older_than_its_lifespan_and_accepts_a_fresh_one()
    {
        var email = await _api.RegisterConfirmedUserAsync();
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await manager.FindByEmailAsync(email))!;
        var purpose = UserManager<User>.ResetPasswordTokenPurpose;
        var dataProtection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();

        PasswordResetTokenProvider Provider(TimeSpan lifespan) => new(
            dataProtection,
            Options.Create(new PasswordResetTokenProviderOptions { TokenLifespan = lifespan }),
            NullLogger<DataProtectorTokenProvider<User>>.Instance);

        // Срок «минус секунда»: токен уже просрочен в момент выпуска. Так проверяем сам механизм истечения.
        var expiredProvider = Provider(TimeSpan.FromSeconds(-1));
        var expired = await expiredProvider.GenerateAsync(purpose, manager, user);
        Assert.False(await expiredProvider.ValidateAsync(purpose, expired, manager, user));

        var freshProvider = Provider(TimeSpan.FromHours(PasswordResetTokenProviderOptions.LifetimeHours));
        var fresh = await freshProvider.GenerateAsync(purpose, manager, user);
        Assert.True(await freshProvider.ValidateAsync(purpose, fresh, manager, user));
    }
}
