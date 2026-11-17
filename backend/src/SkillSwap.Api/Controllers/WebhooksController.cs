using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.LiveKit;

namespace SkillSwap.Api.Controllers;

/// <summary>Входящие webhook внешних сервисов. Доступ только по подписи, а не по токену пользователя.</summary>
[ApiController]
[Route("api/v1/webhooks")]
[Tags("Webhooks")]
public class WebhooksController : ControllerBase
{
    /// <summary>События LiveKit — единицы килобайт; больше — заведомо не они.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    private readonly LiveKitWebhookService _liveKit;

    public WebhooksController(LiveKitWebhookService liveKit)
    {
        _liveKit = liveKit;
    }

    /// <summary>
    /// События медиасервера LiveKit (комната создана, участник вошёл или вышел и т. д.). Принимает только запросы,
    /// подписанные ключом LiveKit: JWT в заголовке Authorization (HS256) с хешем тела (claim sha256).
    /// Каждое событие записывается один раз по уникальному id; повторная доставка тоже отвечает 200.
    /// </summary>
    [HttpPost("livekit")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> LiveKit(CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct);

        // Тело не привязывается к модели: подпись считается по сырым байтам, и любая «переупаковка» её бы испортила.
        var result = await _liveKit.ReceiveAsync(Request.Headers.Authorization.ToString(), body, ct);

        return result switch
        {
            WebhookResult.Accepted or WebhookResult.Duplicate => Ok(),
            WebhookResult.Malformed => throw new BadRequestException("invalid_webhook_payload", "Тело не похоже на событие LiveKit."),
            _ => throw new UnauthorizedException("invalid_webhook_signature", "Подпись webhook недействительна."),
        };
    }

    private async Task<byte[]> ReadBodyAsync(CancellationToken ct)
    {
        if (Request.ContentLength > MaxBodyBytes)
        {
            throw TooLarge();
        }

        // Лимит соблюдаем и при chunked-передаче, где Content-Length нет.
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                throw TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static PayloadTooLargeException TooLarge() =>
        new("payload_too_large", $"Тело запроса больше {MaxBodyBytes / 1024} КБ.");
}
