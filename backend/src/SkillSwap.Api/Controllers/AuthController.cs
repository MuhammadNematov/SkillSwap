using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
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

    public AuthController(AuthService auth, TwoFactorService twoFactor)
    {
        _auth = auth;
        _twoFactor = twoFactor;
    }

    /// <summary>Регистрация по email и паролю. На почту уходит ссылка подтверждения.</summary>
    [HttpPost("register")]
    [ProducesResponseType<RegisterResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
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

    /// <summary>Подтверждение email по одноразовой ссылке из письма.</summary>
    [HttpPost("confirm-email")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmEmail(ConfirmEmailRequest request, CancellationToken ct)
    {
        await _auth.ConfirmEmailAsync(request, ct);
        return NoContent();
    }

    /// <summary>Повторная отправка письма подтверждения. Всегда 204, чтобы не раскрывать, зарегистрирован ли email.</summary>
    [HttpPost("resend-confirmation")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
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
