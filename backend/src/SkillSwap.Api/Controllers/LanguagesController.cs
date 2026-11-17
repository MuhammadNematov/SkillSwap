using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Common;

namespace SkillSwap.Api.Controllers;

/// <summary>Справочник языков занятий для выбора в профиле (раздел 5.2 ТЗ).</summary>
[ApiController]
[Route("api/v1/languages")]
[Tags("Profile")]
public class LanguagesController : ControllerBase
{
    /// <summary>
    /// Все коды ISO 639-1 (две строчные буквы) с названиями на tg, ru и en, по алфавиту кодов; сортировать по названию на нужном
    /// языке должен фронтенд. Это языки уроков; язык интерфейса — отдельное поле профиля (tg, ru или en).
    /// Справочник общий и не меняется между релизами, поэтому его можно кэшировать, и вход не нужен.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<IReadOnlyList<LessonLanguage>>(StatusCodes.Status200OK)]
    public IActionResult List()
    {
        Response.Headers.CacheControl = "public, max-age=86400";
        return Ok(LessonLanguages.All);
    }
}
