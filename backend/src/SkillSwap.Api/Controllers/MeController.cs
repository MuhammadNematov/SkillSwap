using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Profile;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Мой профиль (раздел 12 ТЗ). <c>DELETE /me</c> и <c>POST /me/export</c> появятся на этапе 4 вместе с чатами:
/// им нужно знать, что удалять и выгружать.
/// </summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
[Tags("Profile")]
public class MeController : ControllerBase
{
    private readonly ProfileService _profile;

    public MeController(ProfileService profile)
    {
        _profile = profile;
    }

    /// <summary>Кто я и что в моём профиле. Служебные аккаунты получают тот же ответ без анкеты (языки пусты).</summary>
    [HttpGet]
    [ProducesResponseType<MyProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await _profile.GetMineAsync(Guid.Parse(User.FindFirstValue("sub")!), ct));

    /// <summary>
    /// Заменяет редактируемые поля: имя, о себе, язык интерфейса, часовой пояс (IANA), языки занятий (до 3 из tg, ru, en),
    /// видимость в подборе. Аватар, email и дата рождения здесь не меняются. Служебному аккаунту — 403.
    /// </summary>
    [HttpPut]
    [ProducesResponseType<MyProfileDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update([FromBody] UpdateProfileRequest request, CancellationToken ct) =>
        Ok(await _profile.UpdateMineAsync(Guid.Parse(User.FindFirstValue("sub")!), request, ct));
}
