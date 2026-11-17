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

    public AuthController(AuthService auth)
    {
        _auth = auth;
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
    /// Вход по email и паролю. Access JWT приходит в теле, refresh-токен — в HttpOnly-cookie.
    /// </summary>
    [HttpPost("login")]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request, GetClientInfo(), ct);
        return RespondWithTokens(result);
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
}
