using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Reviews;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// Отзывы после обмена (раздел 8.1 ТЗ). Слепые и взаимные: написанный отзыв скрыт, пока партнёр не напишет свой или не пройдут 7 дней; после публикации
/// править нельзя. Срок написания — 14 дней после закрытия обмена. Доступно только обычным аккаунтам (служебному 403 <c>regular_account_required</c>).
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[Tags("Reviews")]
public class ReviewsController : ControllerBase
{
    private readonly ReviewService _reviews;

    public ReviewsController(ReviewService reviews)
    {
        _reviews = reviews;
    }

    /// <summary>
    /// Отзывы по обмену глазами участника: могу ли я написать (<c>canWrite</c>, иначе <c>unavailableReason</c>: <c>exchange_not_closed</c>, <c>partner_deleted</c>,
    /// <c>no_counted_sessions</c>, <c>review_window_closed</c>, <c>review_already_exists</c>), срок (<c>deadlineAt</c>), отметка «Полный обмен» (<c>isFullExchange</c>),
    /// одностороннее право (<c>oneSided</c>: партнёр не провёл ни одного засчитанного урока, а я пострадал от его неявок; отзыв опубликуется сразу),
    /// мой отзыв (<c>myReview</c>, <c>canEdit</c> пока он не опубликован) и отзыв партнёра обо мне (<c>reviewAboutMe</c>, только после публикации).
    /// Чужой обмен — 404 <c>exchange_not_found</c>.
    /// </summary>
    [HttpGet("exchanges/{exchangeId:guid}/reviews")]
    [ProducesResponseType<ExchangeReviewsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid exchangeId, CancellationToken ct) =>
        Ok(await _reviews.GetStateAsync(UserId(), exchangeId, ct));

    /// <summary>
    /// Оставить отзыв: четыре оценки 1–5 (<c>scoreKnowledge</c>, <c>scoreExplaining</c>, <c>scorePunctuality</c>, <c>scorePoliteness</c>) и <c>text</c> 20–1000 символов.
    /// Право: обмен завершён или прерван (в том числе блокировкой) и была хотя бы одна засчитанная сессия; если сессий не было, но у партнёра три окончательные неявки,
    /// отзыв доступен только пострадавшему. Публикуется сразу, если партнёр уже написал или писать больше некому; иначе через 7 дней после написания.
    /// 201 и мой отзыв. 400: <c>review_score_out_of_range</c>, <c>review_text_too_short</c>, <c>review_text_too_long</c>. 409: <c>review_already_exists</c>,
    /// <c>exchange_not_closed</c>, <c>partner_deleted</c>, <c>no_counted_sessions</c>, <c>review_window_closed</c>.
    /// </summary>
    [HttpPost("exchanges/{exchangeId:guid}/reviews")]
    [ProducesResponseType<ReviewDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid exchangeId, [FromBody] ReviewRequest request, CancellationToken ct)
    {
        var created = await _reviews.CreateAsync(UserId(), exchangeId, request, ct);
        return CreatedAtAction(nameof(Get), new { exchangeId }, created);
    }

    /// <summary>
    /// Изменить свой отзыв: только пока он не опубликован (после публикации 409 <c>review_already_published</c>) и пока не вышел срок (409 <c>review_window_closed</c>).
    /// Срок публикации по неделе от первого написания при правке не сдвигается. Отзыва нет — 404 <c>review_not_found</c>.
    /// </summary>
    [HttpPut("exchanges/{exchangeId:guid}/reviews")]
    [ProducesResponseType<ReviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid exchangeId, [FromBody] ReviewRequest request, CancellationToken ct) =>
        Ok(await _reviews.UpdateAsync(UserId(), exchangeId, request, ct));

    /// <summary>
    /// Опубликованные отзывы о человеке (новые сверху) и его рейтинг: <c>ratingAvg</c> (у «Новичка», меньше трёх отзывов, <c>null</c>), <c>ratingCount</c>, <c>isNewcomer</c>.
    /// Автор удалённого аккаунта подписан «Удалённый пользователь» (<c>author.isDeleted</c>). Видят те же люди, что и профиль: скрытый, заблокированный, удалённый
    /// пользователь, блокировка в любую сторону и другая возрастная группа дают 404 <c>user_not_found</c>.
    /// </summary>
    [HttpGet("users/{userId:guid}/reviews")]
    [ProducesResponseType<UserReviewsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListForUser(Guid userId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await _reviews.ListForUserAsync(UserId(), User.FindFirstValue("role") == "User", userId, page, pageSize, ct));

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
