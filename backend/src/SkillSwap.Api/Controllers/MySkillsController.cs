using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Profile;

namespace SkillSwap.Api.Controllers;

/// <summary>Моя анкета: навыки «Умею» и «Хочу изучать» (раздел 5.2 ТЗ).</summary>
[ApiController]
[Route("api/v1/me/skills")]
[Authorize]
[Tags("Skills")]
public class MySkillsController : ControllerBase
{
    private readonly MySkillsService _skills;

    public MySkillsController(MySkillsService skills)
    {
        _skills = skills;
    }

    /// <summary>
    /// Моя анкета. <c>isComplete</c> — в каждом направлении есть хотя бы один включённый навык: так открывается подбор.
    /// Навык с <c>isActive: false</c> админ выключил после вашего выбора: уберите его при следующем сохранении.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<MySkillsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(await _skills.GetAsync(UserId(), ct));

    /// <summary>
    /// Заменяет анкету целиком: <c>teach</c> («Умею») и <c>learn</c> («Хочу изучать») обязательны, пустой список очищает направление.
    /// У каждого навыка: <c>skillId</c> из каталога, <c>level</c> (Beginner, Intermediate, Advanced), <c>description</c> до 500 символов.
    /// Навык можно указать один раз в каждом направлении, не больше 30 в направлении.
    /// Видимость в подборе: когда анкета становится полной, пользователь показывается сам (<c>isSearchable: true</c>), если только не скрылся
    /// вручную через <c>PUT /me</c>: скрывшегося сервер больше не включает. Если анкета стала неполной, пользователь скрывается (<c>isSearchable: false</c>).
    /// Ошибки: 400 <c>validation_failed</c> (в том числе <c>duplicate_skill</c>), 400 <c>unknown_skill</c> (нет в каталоге или выключен),
    /// 403 <c>staff_has_no_profile</c>, 409 <c>concurrency_conflict</c>.
    /// </summary>
    [HttpPut]
    [ProducesResponseType<MySkillsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Save([FromBody] SaveSkillsRequest request, CancellationToken ct) =>
        Ok(await _skills.SaveAsync(UserId(), request, ct));

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
