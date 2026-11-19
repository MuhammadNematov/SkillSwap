using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Middleware;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Files;

namespace SkillSwap.Api.Controllers;

public sealed class UploadAvatarForm
{
    /// <summary>Картинка png, jpg или webp до 2 МБ.</summary>
    public IFormFile? File { get; set; }
}

/// <summary>
/// Аватар (раздел 5 ТЗ). Сервер удаляет из картинки метаданные (EXIF, GPS), обрезает её до квадрата и уменьшает до 512×512.
/// Готовая картинка отдаётся в <c>avatarUrl</c> профиля подписанной ссылкой на 1 час, и только тому, кто вправе видеть этот профиль (скрытие, блокировки, возрастные группы); иначе там null.
/// </summary>
[ApiController]
[Route("api/v1")]
[Tags("Profile")]
public class AvatarController : ControllerBase
{
    private readonly AvatarService _avatars;

    public AvatarController(AvatarService avatars)
    {
        _avatars = avatars;
    }

    /// <summary>
    /// Загрузить аватар (multipart/form-data, поле <c>file</c>). В ответе новый <c>avatarUrl</c>: подписанная ссылка на 1 час (то же значение придёт в профиле);
    /// постоянного открытого адреса у аватара нет. Ошибки: 400 <c>file_required</c>, <c>unsupported_image</c> (не png/jpg/webp или файл повреждён),
    /// <c>image_too_large</c>; 413 <c>avatar_too_large</c> (больше 2 МБ); 503 <c>storage_unavailable</c>.
    /// </summary>
    [HttpPut("me/avatar")]
    [Authorize]
    [Consumes("multipart/form-data")]
    [UploadSizeLimit(3L * 1024 * 1024, "avatar_too_large", "Аватар не больше 2 МБ.")]
    [RequestSizeLimit(3L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 3L * 1024 * 1024)]
    [ProducesResponseType<AvatarDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Upload([FromForm] UploadAvatarForm form, CancellationToken ct)
    {
        if (form.File is null)
        {
            throw new BadRequestException("file_required", "Приложите файл в поле file.");
        }

        await using var content = form.File.OpenReadStream();
        return Ok(await _avatars.SetAsync(UserId(), new UploadedFile(form.File.FileName, form.File.Length, content), ct));
    }

    /// <summary>Убрать аватар. Если его не было, тоже 204.</summary>
    [HttpDelete("me/avatar")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Remove(CancellationToken ct)
    {
        await _avatars.RemoveAsync(UserId(), ct);
        return NoContent();
    }


    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
