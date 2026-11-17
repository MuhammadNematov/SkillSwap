using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Минимальная версия: нужна, чтобы проверять access-токен (Authorize в Scalar).
/// Полный профиль (GET/PUT/DELETE /me) — этап 3.
/// </summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
[Tags("Profile")]
public class MeController : ControllerBase
{
    /// <summary>Кто я по access-токену.</summary>
    [HttpGet]
    [ProducesResponseType<MeResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<MeResponse> Get()
    {
        return new MeResponse(
            Guid.Parse(User.FindFirstValue("sub")!),
            User.FindFirstValue("email")!,
            User.FindFirstValue("role")!);
    }
}

public sealed record MeResponse(Guid Id, string Email, string Role);
