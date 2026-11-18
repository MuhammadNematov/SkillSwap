using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Common;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.Controllers;

/// <summary>Заявки на обмен (разделы 5.3 и 9.1 ТЗ). Заявку видят только отправитель и получатель, для остальных она не существует (404).</summary>
[ApiController]
[Route("api/v1/exchange-requests")]
[Authorize]
[Tags("Exchange requests")]
public class ExchangeRequestsController : ControllerBase
{
    private readonly ExchangeRequestService _requests;

    public ExchangeRequestsController(ExchangeRequestService requests)
    {
        _requests = requests;
    }

    /// <summary>
    /// Отправить заявку. Обязателен навык получателя (<c>toTeachesSkillId</c>, из его «Умею»); свой навык (<c>fromTeachesSkillId</c>) можно не
    /// указывать: получатель выберет его при принятии. Срок обмена <c>durationDays</c>: 30 или 60. Заявка действует 7 дней.
    /// Ошибки: 400 <c>validation_failed</c>, <c>skill_not_taught_by_recipient</c>, <c>skill_not_taught_by_sender</c>; 403 <c>skills_incomplete</c> (моя анкета
    /// неполная); 404 <c>user_not_found</c> (получатель скрыт, удалён, заблокирован в любую сторону или другой возрастной группы);
    /// 409 <c>request_already_pending</c> и <c>reverse_request_pending</c> (в ответе <c>requestId</c>), <c>request_cooldown</c> (в ответе <c>retryAfter</c>),
    /// <c>too_many_pending_requests</c> (лимит ожидающих исходящих, по умолчанию 5); 429 <c>daily_request_limit</c> (по умолчанию 10 за сутки).
    /// </summary>
    [HttpPost]
    [ProducesResponseType<ExchangeRequestDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create([FromBody] CreateExchangeRequestRequest request, CancellationToken ct)
    {
        var created = await _requests.CreateAsync(UserId(), request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>
    /// Мои заявки: <c>box=in</c> — входящие, <c>box=out</c> — исходящие (обязателен), новые сверху. Необязательный <c>status</c>
    /// (Pending, Accepted, Declined, Withdrawn, Expired), <c>page</c> с 1, <c>pageSize</c> до 50.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<ExchangeRequestDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] string? box, [FromQuery] ExchangeRequestStatus? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = Paging.DefaultPageSize, CancellationToken ct = default) =>
        Ok(await _requests.ListAsync(UserId(), box, status, page, pageSize, ct));

    /// <summary>Одна заявка. Чужая и несуществующая — 404 <c>request_not_found</c>.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ExchangeRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(await _requests.GetAsync(UserId(), id, ct));

    /// <summary>
    /// Принять (только получатель). Если отправитель не указал свой навык, в теле обязателен <c>fromTeachesSkillId</c> из «Умею» отправителя
    /// (400 <c>from_skill_required</c>); если указал, заменить его нельзя (400 <c>from_skill_fixed</c>). Ошибки: 400 <c>skill_not_taught_by_sender</c>;
    /// 403 <c>not_request_recipient</c>; 404 <c>request_not_found</c>; 409 <c>invalid_state_transition</c> (уже не ждёт ответа), <c>request_expired</c>,
    /// <c>skill_no_longer_taught</c>, <c>concurrency_conflict</c> (одновременно изменили: повторить).
    /// </summary>
    [HttpPost("{id:guid}/accept")]
    [ProducesResponseType<ExchangeRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Accept(Guid id, [FromBody] AcceptExchangeRequestRequest? body, CancellationToken ct) =>
        Ok(await _requests.AcceptAsync(UserId(), id, body, ct));

    /// <summary>Отклонить (только получатель, 403 <c>not_request_recipient</c>). Отправитель сможет написать снова через 30 дней.</summary>
    [HttpPost("{id:guid}/decline")]
    [ProducesResponseType<ExchangeRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decline(Guid id, CancellationToken ct) =>
        Ok(await _requests.DeclineAsync(UserId(), id, ct));

    /// <summary>Отозвать (только отправитель, 403 <c>not_request_sender</c>).</summary>
    [HttpPost("{id:guid}/withdraw")]
    [ProducesResponseType<ExchangeRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct) =>
        Ok(await _requests.WithdrawAsync(UserId(), id, ct));

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
