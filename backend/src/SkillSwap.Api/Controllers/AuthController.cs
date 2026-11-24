using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SkillSwap.Api.Auth;
using SkillSwap.Api.Captcha;
using SkillSwap.Api.RateLimiting;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Exceptions;

namespace SkillSwap.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[Tags("Auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    private readonly TwoFactorService _twoFactor;
    private readonly PasswordResetService _passwordReset;

    public AuthController(AuthService auth, TwoFactorService twoFactor, PasswordResetService passwordReset)
    {
        _auth = auth;
        _twoFactor = twoFactor;
        _passwordReset = passwordReset;
    }

    /// <summary>Регистрация по email и паролю. На почту уходит ссылка подтверждения.</summary>
    [HttpPost("register")]
    [EnableRateLimiting(RateLimitingExtensions.RegisterPerIpPolicy)]
    [RequireCaptcha]
    [ProducesResponseType<RegisterResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken ct)
    {
        var response = await _auth.RegisterAsync(request, Request.Headers.AcceptLanguage.ToString(), ct);
        return StatusCode(StatusCodes.Status201Created, response);
    }

    /// <summary>
    /// Первый шаг входа: email и пароль. Если вход завершён, access JWT приходит в теле, а refresh-токен — в HttpOnly-cookie.
    /// Если включена 2FA, придёт <c>requires2fa: true</c> и <c>twoFactorToken</c>: следующий шаг — POST /auth/login/2fa.
    /// Служебным аккаунтам без 2FA придёт <c>requires2faSetup: true</c> и <c>twoFactorToken</c> для настройки.
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting(RateLimitingExtensions.LoginPerIpPolicy)]
    [RequireCaptcha]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request, GetClientInfo(), ct);
        return RespondWithLogin(result);
    }

    /// <summary>Второй шаг входа: код из приложения-аутентификатора или резервный код.</summary>
    [HttpPost("login/2fa")]
    [EnableRateLimiting(RateLimitingExtensions.LoginPerIpPolicy)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> LoginTwoFactor(LoginTwoFactorRequest request, CancellationToken ct)
    {
        var result = await _twoFactor.LoginAsync(request, GetClientInfo(), ct);
        return RespondWithLogin(result);
    }

    /// <summary>
    /// Настройка 2FA, шаг 1: ключ и адрес для QR-кода. Нужен Bearer-токен; служебный аккаунт, который ещё не вошёл,
    /// передаёт <c>twoFactorToken</c> из ответа на /auth/login. Включится 2FA только после /auth/2fa/verify.
    /// </summary>
    [HttpPost("2fa/setup")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.LoginPerIpPolicy)]
    [ProducesResponseType<TwoFactorSetupResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> TwoFactorSetup(TwoFactorSetupRequest? request)
    {
        return Ok(await _twoFactor.SetupAsync(CurrentUserId(), request));
    }

    /// <summary>
    /// Настройка 2FA, шаг 2: первый код из приложения. Включает 2FA и выдаёт 10 резервных кодов (показываются один раз).
    /// Для служебного аккаунта с токеном настройки также завершает вход (в теле <c>auth</c>, refresh — в cookie).
    /// </summary>
    [HttpPost("2fa/verify")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.LoginPerIpPolicy)]
    [ProducesResponseType<TwoFactorVerifyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> TwoFactorVerify(TwoFactorVerifyRequest request, CancellationToken ct)
    {
        var result = await _twoFactor.VerifyAsync(CurrentUserId(), request, GetClientInfo(), ct);
        if (result.RefreshToken is not null)
        {
            RefreshCookie.Set(Response, result.RefreshToken, result.RefreshTokenExpiresAt!.Value);
        }

        return Ok(result.Response);
    }

    /// <summary>Отключение 2FA: пароль и действующий код. Для служебных аккаунтов запрещено.</summary>
    [HttpPost("2fa/disable")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> TwoFactorDisable(TwoFactorDisableRequest request, CancellationToken ct)
    {
        await _twoFactor.DisableAsync(CurrentUserId()!.Value, request, ct);
        return NoContent();
    }

    /// <summary>
    /// Обновление access-токена по refresh-cookie (с ротацией). Фронтенд зовёт это же при загрузке страницы,
    /// чтобы восстановить сеанс.
    /// </summary>
    [HttpPost("refresh")]
    [EnableRateLimiting(RateLimitingExtensions.RefreshPerIpPolicy)]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        Request.Cookies.TryGetValue(RefreshCookie.Name, out var token);
        try
        {
            var result = await _auth.RefreshAsync(token, GetClientInfo(), ct);
            return RespondWithTokens(result);
        }
        catch (AppException)
        {
            // Токен плохой или украден — cookie больше не нужна.
            RefreshCookie.Delete(Response);
            throw;
        }
    }

    /// <summary>Выход: отзывает refresh-токен и удаляет cookie. Всегда 204.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        Request.Cookies.TryGetValue(RefreshCookie.Name, out var token);
        await _auth.LogoutAsync(token, ct);
        RefreshCookie.Delete(Response);
        return NoContent();
    }

    /// <summary>
    /// Подтверждение email шестизначным кодом из письма (единственный способ: ссылки в письме нет). Тело: <c>email</c>, <c>code</c>
    /// (пробелы и дефисы допустимы). Код действует 15 минут, на него 5 попыток, новое письмо («отправить ещё раз») отменяет прежний код.
    /// Любая причина отказа (адреса нет, код неверный, истёк, попытки кончились, почта уже подтверждена) — одна и та же 400 <c>invalid_confirmation_code</c>;
    /// код не шести цифр — 400 <c>validation_failed</c>. Лимит частоты: 20 в час с одного IP, иначе 429.
    /// </summary>
    [HttpPost("confirm-email/code")]
    [EnableRateLimiting(RateLimitingExtensions.ConfirmEmailPerIpPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ConfirmEmailByCode(ConfirmEmailCodeRequest request, CancellationToken ct)
    {
        await _auth.ConfirmEmailByCodeAsync(request, ct);
        return NoContent();
    }

    /// <summary>
    /// Запрос сброса пароля (FR-4): на email уходит ссылка, действующая 1 час. Ответ всегда 204 — и для
    /// зарегистрированного, и для неизвестного email, чтобы нельзя было узнать, кто зарегистрирован.
    /// </summary>
    [HttpPost("forgot-password")]
    [EnableRateLimiting(RateLimitingExtensions.MailPerIpPolicy)]
    [RequireCaptcha]
    [RateLimitByEmail]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await _passwordReset.ForgotPasswordAsync(request, ct);
        return NoContent();
    }

    /// <summary>
    /// Новый пароль по ссылке из письма. Токен одноразовый. После успеха все устройства выходят
    /// (отзываются все refresh-токены), а блокировка входа после неверных паролей снимается; 2FA не отключается.
    /// </summary>
    [HttpPost("reset-password")]
    [EnableRateLimiting(RateLimitingExtensions.ResetPasswordPerIpPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        await _passwordReset.ResetPasswordAsync(request, ct);
        return NoContent();
    }

    /// <summary>Повторная отправка письма подтверждения. Всегда 204, чтобы не раскрывать, зарегистрирован ли email.</summary>
    [HttpPost("resend-confirmation")]
    [EnableRateLimiting(RateLimitingExtensions.MailPerIpPolicy)]
    [RateLimitByEmail]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ResendConfirmation(ResendConfirmationRequest request, CancellationToken ct)
    {
        await _auth.ResendConfirmationAsync(request, ct);
        return NoContent();
    }

    private ClientInfo GetClientInfo() =>
        new(Request.Headers.UserAgent.ToString(), HttpContext.Connection.RemoteIpAddress?.ToString());

    private OkObjectResult RespondWithTokens(AuthResult result)
    {
        RefreshCookie.Set(Response, result.RefreshToken, result.RefreshTokenExpiresAt);
        return Ok(result.Response);
    }

    private OkObjectResult RespondWithLogin(LoginResult result)
    {
        if (result.RefreshToken is not null)
        {
            RefreshCookie.Set(Response, result.RefreshToken, result.RefreshTokenExpiresAt!.Value);
        }

        return Ok(result.Response);
    }

    /// <summary>Id пользователя из access-токена; null, если запрос без (или с плохим) Bearer-токеном.</summary>
    private Guid? CurrentUserId() =>
        Guid.TryParse(User.FindFirstValue("sub"), out var id) ? id : null;
}
