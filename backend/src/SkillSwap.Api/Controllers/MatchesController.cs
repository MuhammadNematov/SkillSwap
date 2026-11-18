using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Common;
using SkillSwap.Application.Matching;

namespace SkillSwap.Api.Controllers;

/// <summary>Подбор пар (раздел 5.3 ТЗ).</summary>
[ApiController]
[Route("api/v1/matches")]
[Authorize]
[Tags("Matches")]
public class MatchesController : ControllerBase
{
    private readonly MatchService _matches;

    public MatchesController(MatchService matches)
    {
        _matches = matches;
    }

    /// <summary>
    /// Подбор. В выдаче те, кто преподаёт то, что я хочу изучать (на уровне не ниже моего), и видим мне: не скрыт, не заблокирован ни в одну
    /// сторону, той же возрастной группы (16–17 лет и 18+, пока включена настройка). Если ему при этом нужно то, что умею я (на уровне не
    /// выше моего), это полный матч (<c>matchType: Full</c>), иначе частичный (<c>Partial</c>: свой навык предложу в тексте заявки).
    /// Общий язык занятий со мной обязателен: без него человека в выдаче нет (если у вас не указан ни один язык, выдача пуста: заполните
    /// языки в профиле). Порядок: полные выше частичных; меньше разница часовых поясов; выше уровень партнёра; выше рейтинг
    /// («Новичок» ниже оценённых); активность за 7 дней; больше совпавших навыков.
    /// Фильтры сужают выдачу: <c>skill</c> (id навыка, он должен быть в моём «Хочу изучать»), <c>lang</c> (ISO 639-1), <c>level</c>
    /// (Beginner, Intermediate, Advanced — уровень, на котором партнёр преподаёт нужный мне навык), <c>minRating</c> (0–5; «Новички» при
    /// фильтре не показываются). <c>page</c> с 1, <c>pageSize</c> до 50.
    /// Ошибки: 403 <c>skills_incomplete</c> (анкета неполная: нужен хотя бы 1 навык в «Умею» и 1 в «Хочу изучать» — фронтенд ведёт на
    /// онбординг), 400 <c>invalid_language</c>, <c>invalid_level</c>, <c>invalid_min_rating</c>, <c>invalid_paging</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<MatchDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Search(
        [FromQuery] Guid? skill,
        [FromQuery] string? lang,
        [FromQuery] string? level,
        [FromQuery] decimal? minRating,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        CancellationToken ct = default)
    {
        var viewerId = Guid.Parse(User.FindFirstValue("sub")!);
        var viewerIsRegular = User.FindFirstValue("role") == "User";
        return Ok(await _matches.SearchAsync(viewerId, viewerIsRegular, new MatchQuery(skill, lang, level, minRating, page, pageSize), ct));
    }
}
