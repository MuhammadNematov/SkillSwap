using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Обращения, апелляции и запросы на блокировку для админа (разделы 3, 8.2 и 8.3 ТЗ). Роли Admin и SuperAdmin; модератор и обычный пользователь получают 403.
/// Админ видит материалы обращения и приложенные сообщения, но не весь чат: доступа к переписке обмена у него нет никогда.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = Policies.Admin)]
[Tags("Admin")]
public class AdminCasesController : ControllerBase
{
    private readonly AdminCaseService _cases;
    private readonly BanRequestService _bans;

    public AdminCasesController(AdminCaseService cases, BanRequestService bans)
    {
        _cases = cases;
        _bans = bans;
    }

    /// <summary>Все обращения (новые сверху) с фильтрами <c>status</c> и <c>type</c> и поиском <c>q</c> (до 100 символов, 400 <c>invalid_search</c>: часть имени или почты любого участника либо текста обращения, без учёта регистра); апелляции, ждущие решения: <c>status=Appealed</c>, идущие проверки: <c>status=AppealReview</c>.</summary>
    [HttpGet("cases")]
    [ProducesResponseType<AdminCaseListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] CaseStatus? status = null, [FromQuery] CaseType? type = null, [FromQuery] string? q = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _cases.ListAsync(AdminId(), status, type, q, page, pageSize, ct));

    /// <summary>
    /// Материалы обращения: описание, оспариваемый отзыв, приложенные сообщения (удалённые и с прежними версиями), пояснения, предупреждения, запросы на бан, апелляция.
    /// Полной переписки обмена здесь нет. Каждый просмотр пишется в аудит (<c>CaseMaterialsRead</c>). Отозванное обращение — 404.
    /// </summary>
    [HttpGet("cases/{caseId:guid}")]
    [ProducesResponseType<AdminCaseDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid caseId, CancellationToken ct) => Ok(await _cases.GetAsync(AdminId(), caseId, ct));

    /// <summary>Действующие модераторы: из них выбирают проверяющего по апелляции.</summary>
    [HttpGet("moderators")]
    [ProducesResponseType<IReadOnlyList<ModeratorOptionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Moderators(CancellationToken ct) => Ok(await _cases.ListModeratorsAsync(AdminId(), ct));

    /// <summary>
    /// Назначить другого модератора на повторную проверку чата (<c>Appealed → AppealReview</c>). Не автор решения, не участник, без конфликта интересов. Проверяющий получает доступ
    /// к чату этого обмена и уведомление <c>AppealReviewAssigned</c>, обе стороны — одно уведомление <c>CaseAppealReviewAssigned</c>. 400: <c>appeal_reviewer_must_differ</c>,
    /// <c>conflict_of_interest</c>. 404: <c>case_not_found</c>, <c>moderator_not_found</c>. 409: <c>not_a_moderator</c>, <c>conflict_of_interest</c>, <c>invalid_state_transition</c>.
    /// </summary>
    [HttpPost("cases/{caseId:guid}/appeal/reviewer")]
    [ProducesResponseType<AdminCaseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AssignReviewer(Guid caseId, [FromBody] AssignReviewerRequest request, CancellationToken ct) =>
        Ok(await _cases.AssignReviewerAsync(AdminId(), caseId, request, ct));

    /// <summary>
    /// Окончательное решение по апелляции (<c>Appealed → Closed</c>): <c>Upheld</c> оставить, <c>Overturned</c> отменить (нужен <c>reason</c>; для спора возвращается <c>ReviewKept</c>,
    /// для жалоб <c>NoViolation</c>), <c>Changed</c> заменить на <c>newDecision</c> (с <c>alsoWarned</c> и <c>reason</c>). Отмена и замена откатывают последствия прежнего решения
    /// (отзыв в рейтинг, предупреждение снимается, обмен размораживается), замена применяет новые; принудительное завершение обмена и одобренную блокировку откатить нельзя.
    /// Обе стороны получают <c>AppealDecided</c> и письмо. 400: <c>case_decision_not_allowed</c>, <c>case_warning_not_combinable</c>, <c>case_reason_too_short</c>,
    /// <c>appeal_final_decision_mismatch</c>. 409: <c>appeal_review_in_progress</c> (ждём заключения), <c>nothing_to_overturn</c>, <c>exchange_not_open</c>, <c>invalid_state_transition</c>.
    /// </summary>
    [HttpPost("cases/{caseId:guid}/appeal/decision")]
    [ProducesResponseType<AdminCaseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decide(Guid caseId, [FromBody] DecideAppealRequest request, CancellationToken ct) =>
        Ok(await _cases.DecideAsync(AdminId(), caseId, request, ct));

    /// <summary>Запросы на блокировку (новые сверху), фильтр <c>status</c>: <c>Pending</c>, <c>Approved</c>, <c>Rejected</c>; поиск <c>q</c> (до 100 символов): часть имени или почты того, на кого запрос, или того, кто его подал, либо текста причины, без учёта регистра.</summary>
    [HttpGet("ban-requests")]
    [ProducesResponseType<BanRequestListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BanRequests(
        [FromQuery] BanRequestStatus? status = null, [FromQuery] string? q = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _bans.ListAsync(AdminId(), status, q, page, pageSize, ct));

    /// <summary>
    /// Одобрить блокировку: аккаунт сразу получает статус <c>Blocked</c>, все его сеансы отзываются, открытые соединения SignalR обрываются, активные и замороженные обмены прерываются
    /// (партнёру причина «партнёр недоступен», открытые уроки отменяются). Запросившему модератору приходит <c>BanRequestDecided</c>. 404: <c>ban_request_not_found</c>.
    /// 409 <c>invalid_state_transition</c>: уже решён; 400 <c>user_not_active</c>: аккаунт уже не действующий.
    /// </summary>
    [HttpPost("ban-requests/{id:guid}/approve")]
    [ProducesResponseType<BanRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(Guid id, [FromBody] DecideBanRequest? request, CancellationToken ct) =>
        Ok(await _bans.ApproveAsync(AdminId(), id, request, ct));

    /// <summary>Отклонить запрос. Обмен, замороженный по этому обращению, размораживается, если по нему нет других незакрытых жалоб. 409 <c>invalid_state_transition</c>: уже решён.</summary>
    [HttpPost("ban-requests/{id:guid}/reject")]
    [ProducesResponseType<BanRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] DecideBanRequest? request, CancellationToken ct) =>
        Ok(await _bans.RejectAsync(AdminId(), id, request, ct));

    private Guid AdminId() => Guid.Parse(User.FindFirstValue("sub")!);
}
