using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
}
