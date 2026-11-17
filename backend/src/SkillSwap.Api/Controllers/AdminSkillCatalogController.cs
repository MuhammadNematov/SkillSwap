using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Common;
using SkillSwap.Application.Skills;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Правка каталога навыков админом (раздел 5.2 ТЗ). Доступно админу и суперадмину; модератору и пользователю — 403.
/// Здесь видны и выключенные записи. Удаляется только то, на что никто не ссылается; остальное выключают.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Roles = "Admin,SuperAdmin")]
[Tags("Admin")]
public class AdminSkillCatalogController : ControllerBase
{
    private readonly SkillCatalogAdminService _catalog;

    public AdminSkillCatalogController(SkillCatalogAdminService catalog)
    {
        _catalog = catalog;
    }

    // ---------- категории ----------

    /// <summary>Все категории (и выключенные) с числом навыков.</summary>
    [HttpGet("skill-categories")]
    [ProducesResponseType<IReadOnlyList<AdminCategoryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListCategories(CancellationToken ct) =>
        Ok(await _catalog.ListCategoriesAsync(ct));

    [HttpGet("skill-categories/{id:guid}")]
    [ProducesResponseType<AdminCategoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCategory(Guid id, CancellationToken ct) =>
        Ok(await _catalog.GetCategoryAsync(id, ct));

    /// <summary>Новая категория. 409 <c>category_name_taken</c>, если английское название занято (без учёта регистра).</summary>
    [HttpPost("skill-categories")]
    [ProducesResponseType<AdminCategoryDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateCategory([FromBody] CategoryCreateRequest request, CancellationToken ct)
    {
        var created = await _catalog.CreateCategoryAsync(request, ct);
        return CreatedAtAction(nameof(GetCategory), new { id = created.Id }, created);
    }

    /// <summary>Заменяет категорию целиком (<c>isActive</c> обязателен). Выключенная категория скрывает свои навыки.</summary>
    [HttpPut("skill-categories/{id:guid}")]
    [ProducesResponseType<AdminCategoryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateCategory(Guid id, [FromBody] CategoryUpdateRequest request, CancellationToken ct) =>
        Ok(await _catalog.UpdateCategoryAsync(id, request, ct));

    /// <summary>Удаляет пустую категорию. Если в ней есть навыки — 409 <c>category_not_empty</c>.</summary>
    [HttpDelete("skill-categories/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct)
    {
        await _catalog.DeleteCategoryAsync(id, ct);
        return NoContent();
    }

    // ---------- навыки ----------

    /// <summary>Навыки для админа: и включённые, и выключенные (<c>isActive</c> сужает), с синонимами. Страницы до 50.</summary>
    [HttpGet("skills")]
    [ProducesResponseType<PagedResult<AdminSkillDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListSkills(
        [FromQuery] Guid? category,
        [FromQuery] string? q,
        [FromQuery] bool? isActive,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        CancellationToken ct = default) =>
        Ok(await _catalog.SearchSkillsAsync(category, q, isActive, page, pageSize, ct));

    [HttpGet("skills/{id:guid}")]
    [ProducesResponseType<AdminSkillDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSkill(Guid id, CancellationToken ct) =>
        Ok(await _catalog.GetSkillAsync(id, ct));

    /// <summary>Новый навык. 404 <c>category_not_found</c>; 409 <c>skill_name_taken</c>, если в категории уже есть такой (без учёта регистра).</summary>
    [HttpPost("skills")]
    [ProducesResponseType<AdminSkillDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateSkill([FromBody] SkillCreateRequest request, CancellationToken ct)
    {
        var created = await _catalog.CreateSkillAsync(request, ct);
        return CreatedAtAction(nameof(GetSkill), new { id = created.Id }, created);
    }

    /// <summary>Заменяет навык целиком (<c>isActive</c> обязателен); можно перенести в другую категорию.</summary>
    [HttpPut("skills/{id:guid}")]
    [ProducesResponseType<AdminSkillDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateSkill(Guid id, [FromBody] SkillUpdateRequest request, CancellationToken ct) =>
        Ok(await _catalog.UpdateSkillAsync(id, request, ct));

    /// <summary>Удаляет навык. Если он указан в анкетах — 409 <c>skill_in_use</c>: тогда его выключают.</summary>
    [HttpDelete("skills/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteSkill(Guid id, CancellationToken ct)
    {
        await _catalog.DeleteSkillAsync(id, ct);
        return NoContent();
    }
}
