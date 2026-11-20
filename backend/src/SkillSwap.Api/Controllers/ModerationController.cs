using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Auth;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Рабочее место модератора (разделы 3, 8.2 и 10.2 ТЗ). Только роль Moderator: админ, суперадмин и обычный пользователь получают 403.
/// Доступ к чату обмена появляется при взятии обращения, закрывается при решении, даёт только чтение, каждое чтение попадает в журнал аудита.
/// </summary>
[ApiController]
[Route("api/v1/moderation")]
[Authorize(Policy = Policies.Moderator)]
[Tags("Moderation")]
public class ModerationController : ControllerBase
{
    private readonly ModerationService _moderation;

    public ModerationController(ModerationService moderation)
    {
        _moderation = moderation;
    }

    /// <summary>
    /// Очередь и мои обращения. <c>scope=Queue</c> (по умолчанию): не взятые, сначала высокий приоритет, затем ближайший срок реакции (<c>dueAt</c>, <c>isOverdue</c>);
    /// <c>scope=Mine</c>: взятые мной и не закрытые. Обращения, где у модератора конфликт интересов (его личный аккаунт или бывший партнёр личного аккаунта по обмену),
    /// не показываются. Фильтры <c>type</c> и <c>priority</c>.
    /// </summary>
    [HttpGet("cases")]
    [ProducesResponseType<ModerationCaseListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] ModeratorScope scope = ModeratorScope.Queue, [FromQuery] CaseType? type = null, [FromQuery] CasePriority? priority = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _moderation.ListAsync(ModeratorId(), scope, type, priority, page, pageSize, ct));

    /// <summary>
    /// Обращение: из очереди или взятое мной (<c>hasChatAccess</c> — есть ли у меня доступ к чату). Чужое взятое обращение и обращение с конфликтом интересов — 404 <c>case_not_found</c>.
    /// Для спора по отзыву приходит оспариваемый отзыв (<c>review</c>).
    /// </summary>
    [HttpGet("cases/{caseId:guid}")]
    [ProducesResponseType<ModerationCaseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid caseId, CancellationToken ct) => Ok(await _moderation.GetAsync(ModeratorId(), caseId, ct));

    /// <summary>
    /// Взять обращение в работу. Участникам обращения приходит по одному уведомлению <c>CaseTaken</c> (оно говорит, что модератор может прочитать переписку этого обмена),
    /// модератор получает доступ к чату именно этого обмена. 403 <c>conflict_of_interest</c>: в обращении участвует личный аккаунт модератора или его бывший партнёр по обмену.
    /// 409 <c>invalid_state_transition</c>: уже взято или отозвано.
    /// </summary>
    [HttpPost("cases/{caseId:guid}/take")]
    [ProducesResponseType<ModerationCaseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Take(Guid caseId, CancellationToken ct) => Ok(await _moderation.TakeAsync(ModeratorId(), caseId, ct));

    /// <summary>
    /// История чата обмена по взятому обращению (курсор и <c>limit</c> 1–100, как в обычном чате). Удалённые сообщения приходят с текстом (<c>deletedAt</c>),
    /// у исправленных есть прежние версии (<c>revisions</c>), сообщения-доказательства помечены <c>isEvidence</c>. Каждый запрос записывается в аудит (<c>ChatRead</c>).
    /// Только чтение. 403 <c>chat_access_denied</c>: обращение взял не этот модератор, доступ уже закрыт решением или его не было. 404 <c>case_not_found</c>.
    /// </summary>
    [HttpGet("cases/{caseId:guid}/chat")]
    [ProducesResponseType<ModerationChatPageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Chat(Guid caseId, [FromQuery] string? before, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(await _moderation.ReadChatAsync(ModeratorId(), caseId, before, limit, ct));

    /// <summary>
    /// Запросить пояснения: вопрос заявителю, обвиняемому или обоим (<c>recipient</c>: <c>Opener</c>, <c>Against</c>, <c>Both</c>; <c>text</c> 1–2000 символов).
    /// Обращение переходит в <c>WaitingResponse</c>, у него появляется <c>responseDeadlineAt</c> (48 часов), получатели получают уведомление <c>CaseExplanationRequested</c>.
    /// Когда ответят все или срок выйдет, обращение вернётся в работу само. Не ваше или не взятое — 404; уже ждём ответов — 409 <c>explanation_already_requested</c>;
    /// больше 10 вопросов — 409 <c>too_many_questions</c>; обращение не в работе — 409 <c>invalid_state_transition</c>.
    /// </summary>
    [HttpPost("cases/{caseId:guid}/questions")]
    [ProducesResponseType<ExplanationThreadDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Ask(Guid caseId, [FromBody] AskQuestionRequest request, [FromServices] ExplanationService explanations, CancellationToken ct) =>
        Ok(await explanations.AskAsync(ModeratorId(), caseId, request, ct));

    /// <summary>Вся переписка по моему обращению: вопросы обоим участникам и их ответы.</summary>
    [HttpGet("cases/{caseId:guid}/explanations")]
    [ProducesResponseType<ExplanationThreadDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Explanations(Guid caseId, [FromServices] ExplanationService explanations, CancellationToken ct) =>
        Ok(await explanations.ListForModeratorAsync(ModeratorId(), caseId, ct));

    /// <summary>
    /// Вынести решение. Спор по отзыву: <c>ReviewKept</c> (отзыв возвращается в рейтинг) или <c>ReviewHidden</c> (скрыт, рейтинг пересчитан). Жалобы: <c>Warning</c>,
    /// <c>ExchangeEnded</c> (обмен завершается принудительно, у обоих право на отзыв по обычным правилам), <c>FrozenBanRequested</c> (обмен заморожен, запрос на бан админу),
    /// <c>NoViolation</c> (обмен, замороженный жалобой, размораживается). <c>alsoWarned</c> добавляет предупреждение виновному; третье действующее за 12 месяцев само создаёт запрос на бан.
    /// <c>reason</c> 20–2000 символов, его видят обе стороны. Доступ модератора к чату закрывается, обе стороны получают уведомление <c>CaseResolved</c> и письмо,
    /// через 7 дней без апелляции обращение закрывается. 400: <c>case_decision_not_allowed</c>, <c>case_warning_not_combinable</c>, <c>case_reason_too_short</c>/<c>_too_long</c>.
    /// 409: <c>waiting_for_explanations</c>, <c>exchange_not_open</c> (завершать нечего), <c>invalid_state_transition</c>.
    /// </summary>
    [HttpPost("cases/{caseId:guid}/resolve")]
    [ProducesResponseType<ModerationCaseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resolve(Guid caseId, [FromBody] ResolveCaseRequest request, [FromServices] ResolutionService resolution, CancellationToken ct) =>
        Ok(await resolution.ResolveAsync(ModeratorId(), caseId, request, ct));

    /// <summary>Заморозить обмен по жалобе, пока она рассматривается (чат только для чтения, открытые уроки отменяются, обоим <c>ExchangeFrozen</c>). 409: <c>exchange_not_active</c>, <c>case_type_not_supported</c>, <c>case_not_in_work</c>.</summary>
    [HttpPost("cases/{caseId:guid}/freeze-exchange")]
    [ProducesResponseType<ExchangeFreezeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> FreezeExchange(Guid caseId, [FromServices] ResolutionService resolution, CancellationToken ct) =>
        Ok(await resolution.FreezeExchangeAsync(ModeratorId(), caseId, ct));

    /// <summary>Разморозить обмен (срок обмена сдвигается на время заморозки, обоим <c>ExchangeUnfrozen</c>). 409: <c>exchange_not_frozen</c>, <c>case_type_not_supported</c>, <c>case_not_in_work</c>.</summary>
    [HttpPost("cases/{caseId:guid}/unfreeze-exchange")]
    [ProducesResponseType<ExchangeFreezeDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UnfreezeExchange(Guid caseId, [FromServices] ResolutionService resolution, CancellationToken ct) =>
        Ok(await resolution.UnfreezeExchangeAsync(ModeratorId(), caseId, ct));

    private Guid ModeratorId() => Guid.Parse(User.FindFirstValue("sub")!);
}
