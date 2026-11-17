using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Auth;

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
}
