using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using SkillSwap.Api.RateLimiting;
using SkillSwap.Application.Common;
using SkillSwap.Application.Skills;

namespace SkillSwap.Api.Controllers;

/// <summary>Каталог навыков «Категория → Навык» (раздел 5.2 ТЗ). Читать каталог может любой вошедший пользователь.</summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[Tags("Skills")]
public class SkillsController : ControllerBase
{
    private readonly SkillCatalogService _catalog;

    public SkillsController(SkillCatalogService catalog)
    {
        _catalog = catalog;
    }

    /// <summary>Включённые категории в порядке <c>sortOrder</c>. Названия на всех трёх языках, язык выбирает фронтенд.</summary>
    [HttpGet("skill-categories")]
    [ProducesResponseType<IReadOnlyList<SkillCategoryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Categories(CancellationToken ct) =>
        Ok(await _catalog.ListCategoriesAsync(ct));

    /// <summary>
    /// Поиск навыков. <c>q</c> — часть названия (на tg, ru или en) или синонима, без учёта регистра; <c>category</c> — id категории;
    /// <c>lang</c> — язык сортировки (по умолчанию en); <c>page</c> с 1, <c>pageSize</c> до 50.
    /// </summary>
    [HttpGet("skills")]
    [ProducesResponseType<PagedResult<SkillDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Search(
        [FromQuery] Guid? category,
        [FromQuery] string? q,
        [FromQuery] string? lang,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        CancellationToken ct = default) =>
        Ok(await _catalog.SearchAsync(category, q, lang, page, pageSize, ct));

    /// <summary>
    /// Предложить новый навык, которого нет в каталоге. Предложение попадает в очередь админа (одобрить, объединить с существующим или
    /// отклонить). Лимиты: 5 предложений в час и не больше 10 неразобранных одновременно.
    /// Ошибки: 400 <c>validation_failed</c> (название 2–100 символов с буквой), 400 <c>unknown_category</c>, 409 <c>skill_already_exists</c>
    /// (такой навык уже есть в категории, в том числе как синоним), 409 <c>suggestion_already_pending</c>,
    /// 409 <c>too_many_pending_suggestions</c>, 403 <c>regular_account_required</c>, 429 <c>rate_limited</c>.
    /// </summary>
    [HttpPost("skills/suggestions")]
    [EnableRateLimiting(RateLimitingExtensions.SuggestionsPerUserPolicy)]
    [ProducesResponseType<SkillSuggestionDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Suggest(
        [FromBody] SuggestSkillRequest request, [FromServices] SkillSuggestionService suggestions, CancellationToken ct)
    {
        var created = await suggestions.SuggestAsync(Guid.Parse(User.FindFirstValue("sub")!), request, ct);
        return StatusCode(StatusCodes.Status201Created, created);
    }
}
