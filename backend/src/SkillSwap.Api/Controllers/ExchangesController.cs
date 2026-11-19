using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exchanges;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Обмены (разделы 6.1 и 9.2 ТЗ). Обмен создаётся при принятии заявки. Доступ только у участников: чужой и несуществующий обмен — 404
/// <c>exchange_not_found</c>. Все поля описаны глазами вызывающего: «я» и «партнёр».
/// </summary>
[ApiController]
[Route("api/v1/exchanges")]
[Authorize]
[Tags("Exchanges")]
public class ExchangesController : ControllerBase
{
    private readonly ExchangeService _exchanges;

    public ExchangesController(ExchangeService exchanges)
    {
        _exchanges = exchanges;
    }

    /// <summary>Мои обмены, новые сверху. Необязательный <c>status</c> (Active, Completed, Aborted, Frozen), <c>page</c> с 1, <c>pageSize</c> до 50.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<ExchangeDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] ExchangeStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = Paging.DefaultPageSize, CancellationToken ct = default) =>
        Ok(await _exchanges.ListAsync(UserId(), status, page, pageSize, ct));

    /// <summary>Карточка обмена: партнёр, навыки, сроки, кто нажал «Завершить», согласие на продление, баланс уроков.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ExchangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(await _exchanges.GetAsync(UserId(), id, ct));

    /// <summary>
    /// «Завершить». Обмен становится Completed, когда нажали оба; до этого <c>completedByMe</c> = true. Повторное нажатие не ошибка.
    /// Обмен не активен — 409 <c>invalid_state_transition</c>.
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType<ExchangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct) =>
        Ok(await _exchanges.CompleteAsync(UserId(), id, ct));

    /// <summary>
    /// Выйти досрочно: нужна причина (до 500 символов, 400 <c>validation_failed</c>). Обмен становится Aborted, виновный — я (<c>abortedBy: me</c>).
    /// Прервать можно только активный обмен (замороженный закрывает модератор): иначе 409 <c>invalid_state_transition</c>.
    /// <para>
    /// <b>Выход из-за неявок партнёра:</b> причина ровно <c>partner_no_show</c> (регистр не важен). Разрешено, когда у партнёра в этом обмене не меньше трёх
    /// окончательных неявок (<c>partnerNoShows</c> и <c>canAbortForNoShow</c> в обмене), иначе 409 <c>no_show_threshold_not_reached</c> (<c>partnerNoShows</c>, <c>required</c>).
    /// Виновным записывается партнёр: у вышедшего <c>abortedBy: partner</c>, у партнёра <c>me</c>; <c>abortReason</c> = <c>partner_no_show</c>,
    /// <c>abortedForNoShow</c> = true (на этом признаке этап 6 даст пострадавшему одностороннее право на отзыв). Партнёру приходит уведомление <c>ExchangeAborted</c>.
    /// </para>
    /// </summary>
    [HttpPost("{id:guid}/abort")]
    [ProducesResponseType<ExchangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Abort(Guid id, [FromBody] AbortExchangeRequest? request, CancellationToken ct) =>
        Ok(await _exchanges.AbortAsync(UserId(), id, request, ct));

    /// <summary>
    /// Согласиться на продление на 30 дней. Продление применяется, когда согласны оба (до этого <c>myExtensionAccepted</c> = true,
    /// <c>endDate</c> прежний); после применения <c>isExtended</c> = true. Одно продление за обмен: повторное — 400 <c>already_extended</c>.
    /// </summary>
    [HttpPost("{id:guid}/extend")]
    [ProducesResponseType<ExchangeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Extend(Guid id, CancellationToken ct) =>
        Ok(await _exchanges.ExtendAsync(UserId(), id, ct));

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
