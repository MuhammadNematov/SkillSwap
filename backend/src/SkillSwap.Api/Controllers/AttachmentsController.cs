using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Api.Middleware;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Files;

namespace SkillSwap.Api.Controllers;

public sealed class UploadAttachmentForm
{
    /// <summary>Файл: pdf, docx, xlsx, pptx, zip, txt, png, jpg, webp (до 20 МБ) или голосовое .webm с Opus (до 4 МБ).</summary>
    public IFormFile? File { get; set; }

    /// <summary>Длительность голосового в секундах (1–120). Обязательна для .webm, для остальных файлов игнорируется.</summary>
    public int? DurationSeconds { get; set; }
}

/// <summary>
/// Файлы и голосовые в чате (разделы 7.1 и 10.1 ТЗ). Файл проверяется (белый список, сигнатуры), хранится в закрытом бакете и
/// отдаётся подписанной ссылкой на 5 минут.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize]
[Tags("Chat")]
public class AttachmentsController : ControllerBase
{
    private const long RequestLimit = 22L * 1024 * 1024;   // файл до 20 МБ и немного на служебные части формы

    private readonly AttachmentService _attachments;

    public AttachmentsController(AttachmentService attachments)
    {
        _attachments = attachments;
    }

    /// <summary>
    /// Отправить файл или голосовое в чат обмена (multipart/form-data: <c>file</c>, для голосового ещё <c>durationSeconds</c>). Создаётся сообщение
    /// типа <c>File</c> или <c>Voice</c> с полем <c>attachment</c>; собеседнику оно приходит событием <c>message</c> в хабе. Картинки очищаются
    /// от метаданных (EXIF, GPS). Нужен активный обмен и лимит сообщений (как у текста).
    /// Ошибки: 400 <c>file_required</c>, <c>unsupported_file_type</c> (не из белого списка), <c>file_type_mismatch</c> (содержимое не соответствует
    /// расширению), <c>file_empty</c>, <c>invalid_file_name</c>, <c>invalid_duration</c>, <c>voice_too_long</c>, <c>image_too_large</c>; 413 <c>file_too_large</c>;
    /// 409 <c>chat_read_only</c>; 404 <c>exchange_not_found</c>; 429 <c>chat_rate_limited</c>; 503 <c>storage_unavailable</c> / <c>storage_not_configured</c>.
    /// </summary>
    [HttpPost("exchanges/{exchangeId:guid}/attachments")]
    [Consumes("multipart/form-data")]
    [UploadSizeLimit(RequestLimit, "file_too_large", "Файл больше 20 МБ.")]
    [RequestSizeLimit(RequestLimit)]
    [RequestFormLimits(MultipartBodyLengthLimit = RequestLimit)]
    [ProducesResponseType<MessageDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Upload(Guid exchangeId, [FromForm] UploadAttachmentForm form, CancellationToken ct)
    {
        if (form.File is null)
        {
            throw new BadRequestException("file_required", "Приложите файл в поле file.");
        }

        await using var content = form.File.OpenReadStream();
        var message = await _attachments.UploadAsync(
            UserId(), exchangeId, new UploadedFile(form.File.FileName, form.File.Length, content), form.DurationSeconds, ct);
        return Created($"/api/v1/exchanges/{exchangeId}/messages", message);
    }

    /// <summary>
    /// Подписанная ссылка на скачивание: действует 5 минут, браузер сохраняет файл под его именем (<c>Content-Disposition: attachment</c>). Ссылку
    /// запрашивайте перед скачиванием, а не при показе списка. Только участникам обмена, пока чат им доступен; вложение удалённого сообщения
    /// недоступно. Чужое и несуществующее — 404 <c>attachment_not_found</c>; через 30 дней после закрытия обмена — 403 <c>chat_archived</c>.
    /// </summary>
    [HttpGet("attachments/{id:guid}/url")]
    [ProducesResponseType<AttachmentUrlDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetDownloadUrl(Guid id, CancellationToken ct) =>
        Ok(await _attachments.GetUrlAsync(UserId(), id, ct));

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
