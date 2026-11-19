using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Moderation;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Обращения к модератору (раздел 8.2 ТЗ): спор по отзыву, жалоба на поведение, жалоба на неявки и срыв. Только обычные аккаунты
/// (служебному 403 <c>regular_account_required</c>). Обвиняемый видит обращение, когда его взяли в работу, а не при подаче.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[Tags("Cases")]
public class CasesController : ControllerBase
{
    private readonly CaseService _cases;

    public CasesController(CaseService cases)
    {
        _cases = cases;
    }

    /// <summary>
    /// Подать обращение. <c>type</c>: <c>ReviewDispute</c> (нужен <c>reviewId</c>: опубликованный отзыв обо мне, срок 14 дней от публикации; отзыв замораживается и не влияет на рейтинг),
    /// <c>Conduct</c> (до 90 дней после обмена; высокий приоритет), <c>NoShowAbuse</c> (30 дней после обмена). <c>category</c> зависит от типа. <c>description</c> 20–2000 символов,
    /// <c>messageIds</c> до 20 сообщений этого обмена как доказательства. <c>fromCall: true</c> (только <c>Conduct</c>): жалоба из видеозвонка, нужно быть в комнате идущего урока;
    /// обмен замораживается сразу, комната закрывается, открытые уроки отменяются. Приоритет высокий при жалобе на поведение, из звонка и когда один из двоих младше 18.
    /// 201 и обращение. 400: <c>case_description_too_short</c>/<c>_too_long</c>, <c>case_category_mismatch</c>, <c>case_review_required</c>, <c>case_review_not_allowed</c>,
    /// <c>case_from_call_conduct_only</c>, <c>too_many_evidence</c>, <c>evidence_not_in_exchange</c>. 404: <c>exchange_not_found</c>, <c>review_not_found</c>.
    /// 409: <c>partner_deleted</c>, <c>filing_window_closed</c>, <c>case_already_open</c>, <c>case_already_exists</c>, <c>review_already_disputed</c>, <c>too_many_open_cases</c> (больше 3 в работе),
    /// <c>not_in_call</c>. 429: <c>case_rate_limited</c> (больше 5 за сутки).
    /// </summary>
    [HttpPost("cases")]
    [ProducesResponseType<CaseDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Open([FromBody] OpenCaseRequest request, CancellationToken ct)
    {
        var created = await _cases.OpenAsync(UserId(), request, ct);
        return CreatedAtAction(nameof(Get), new { caseId = created.Id }, created);
    }

    /// <summary>
    /// Мои обращения (новые сверху): <c>role=Opened</c> — которые подал я, <c>role=Against</c> — поданные на меня (видны, когда их взяли в работу), <c>All</c> (по умолчанию) — оба списка.
    /// </summary>
    [HttpGet("me/cases")]
    [ProducesResponseType<CaseListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListMine(
        [FromQuery] CaseListFilter role = CaseListFilter.All, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _cases.ListMineAsync(UserId(), role, page, pageSize, ct));

    /// <summary>Обращение. Заявитель видит своё всегда, тот, на кого оно подано, — после взятия в работу; чужое и отозванное для него — 404 <c>case_not_found</c>.</summary>
    [HttpGet("cases/{caseId:guid}")]
    [ProducesResponseType<CaseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid caseId, CancellationToken ct) => Ok(await _cases.GetAsync(UserId(), caseId, ct));

    /// <summary>
    /// Отозвать своё обращение, пока его не взяли в работу (<c>canWithdraw</c>). Замороженный спором отзыв возвращается в рейтинг, обмен, замороженный жалобой из звонка, размораживается.
    /// Не заявителю — 404 <c>case_not_found</c>; уже взято в работу — 409 <c>invalid_state_transition</c>.
    /// </summary>
    [HttpPost("cases/{caseId:guid}/withdraw")]
    [ProducesResponseType<CaseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Withdraw(Guid caseId, CancellationToken ct) => Ok(await _cases.WithdrawAsync(UserId(), caseId, ct));

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
