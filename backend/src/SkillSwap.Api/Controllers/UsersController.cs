using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Profile;

namespace SkillSwap.Api.Controllers;

/// <summary>Профили других пользователей (раздел 12 ТЗ). Блокировка пользователей добавляется отдельным шагом.</summary>
[ApiController]
[Route("api/v1/users")]
[Authorize]
[Tags("Profile")]
public class UsersController : ControllerBase
{
    private readonly ProfileService _profile;

    public UsersController(ProfileService profile)
    {
        _profile = profile;
    }

    /// <summary>
    /// Публичный профиль. Если профиля нет, пользователь скрыт, заблокирован, удалён или это служебный аккаунт — везде одинаковый 404.
    /// Служебному аккаунту чужие профили недоступны (403).
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<PublicProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var viewerId = Guid.Parse(User.FindFirstValue("sub")!);
        var viewerIsRegular = User.FindFirstValue("role") == "User";
        return Ok(await _profile.GetPublicAsync(viewerId, viewerIsRegular, id, ct));
    }
}
