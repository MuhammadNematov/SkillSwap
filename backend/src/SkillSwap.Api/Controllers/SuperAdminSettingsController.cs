using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Settings;

namespace SkillSwap.Api.Controllers;

/// <summary>Системные настройки (раздел 3 ТЗ): только суперадмин. Менять можно лимиты антиспама и возрастную политику подбора, значения действуют сразу.</summary>
[ApiController]
[Route("api/v1/superadmin/settings")]
[Authorize(Policy = Policies.SuperAdmin)]
[Tags("Superadmin settings")]
public class SuperAdminSettingsController : ControllerBase
{
    private readonly SystemSettingsAdminService _settings;

    public SuperAdminSettingsController(SystemSettingsAdminService settings)
    {
        _settings = settings;
    }

    /// <summary>Все настройки: ключ, текущее значение, описание, тип (`Bool`, `Int`), значение по умолчанию, границы для чисел, когда меняли и по умолчанию ли значение.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SettingDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await _settings.ListAsync(ActorId(), ct));

    /// <summary>
    /// Изменить настройки: тело `{ settings: [{ key, value }], reason?, confirmation }`. Применяются все или ни одной; нужен пароль и код 2FA на каждый запрос. В аудит пишется прежнее и
    /// новое значение каждой изменённой настройки (`SettingChanged`). Ответ: все настройки и ключи, которые реально изменились. 400 `no_settings`, `unknown_setting`, `duplicate_setting`,
    /// `invalid_setting_value` (в ответе `key`, `kind`, `min`, `max`), `reason_too_long` и ошибки подтверждения.
    /// </summary>
    [HttpPut]
    [ProducesResponseType<UpdateSettingsResultDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update([FromBody] UpdateSettingsRequest request, CancellationToken ct) => Ok(await _settings.UpdateAsync(ActorId(), request, ct));

    private Guid ActorId() => Guid.Parse(User.FindFirstValue("sub")!);
}
