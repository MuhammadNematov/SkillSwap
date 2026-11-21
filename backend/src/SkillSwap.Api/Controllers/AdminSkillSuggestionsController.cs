using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Skills;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Рассмотрение предложений навыков (раздел 5.2 ТЗ): одобрить (создаётся навык), объединить с существующим или отклонить. Автор получает уведомление. Доступно админу и суперадмину.
/// Остальные неразобранные предложения с тем же названием в той же категории разбираются вместе с этим как объединённые.
/// </summary>
[ApiController]
[Route("api/v1/admin/skill-suggestions")]
[Authorize(Policy = Policies.Admin)]
[Tags("Admin skill suggestions")]
public class AdminSkillSuggestionsController : ControllerBase
{
    private readonly SkillSuggestionAdminService _suggestions;

    public AdminSkillSuggestionsController(SkillSuggestionAdminService suggestions)
    {
        _suggestions = suggestions;
    }

    /// <summary>
    /// Очередь предложений: по умолчанию неразобранные (старые сверху), `status` (`Pending`, `Approved`, `Merged`, `Rejected`) и `categoryId` сужают. У неразобранного: автор, категория,
    /// сколько таких же ждёт и похожие навыки каталога (до 5) для объединения.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<AdminSuggestionListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] SuggestionStatus? status = null, [FromQuery] Guid? categoryId = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _suggestions.ListAsync(AdminId(), status, categoryId, page, pageSize, ct));

    /// <summary>
    /// Одобрить: тело `{ nameTg, nameRu, nameEn, synonyms?, categoryId? }`, все три названия обязательны. Создаётся навык, автору приходит `SkillSuggestionApproved`.
    /// 400 `name_required`, `name_too_long`, `synonym_too_long`, `too_many_synonyms`; 404 `suggestion_not_found`, `category_not_found`; 409 `skill_name_taken`, `invalid_state_transition`.
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType<AdminSuggestionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ApproveSuggestionRequest request, CancellationToken ct) => Ok(await _suggestions.ApproveAsync(AdminId(), id, request, ct));

    /// <summary>
    /// Объединить с существующим навыком: тело `{ skillId, addAsSynonym? }` (по умолчанию предложенное название добавляется синонимом). Автору приходит `SkillSuggestionMerged`.
    /// 404 `suggestion_not_found`, `skill_not_found`; 409 `skill_not_active`, `invalid_state_transition`.
    /// </summary>
    [HttpPost("{id:guid}/merge")]
    [ProducesResponseType<AdminSuggestionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Merge(Guid id, [FromBody] MergeSuggestionRequest request, CancellationToken ct) => Ok(await _suggestions.MergeAsync(AdminId(), id, request, ct));

    /// <summary>Отклонить: тело `{ reason? }` (до 500 символов, её увидит автор в `SkillSuggestionRejected`). 400 `reject_reason_too_long`; 404 `suggestion_not_found`; 409 `invalid_state_transition`.</summary>
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType<AdminSuggestionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectSuggestionRequest? request, CancellationToken ct) => Ok(await _suggestions.RejectAsync(AdminId(), id, request, ct));

    private Guid AdminId() => Guid.Parse(User.FindFirstValue("sub")!);
}
