using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using SkillSwap.Application.Options;
using SkillSwap.Application.Telegram;
using SkillSwap.Infrastructure.Telegram;
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
    private readonly TelegramUpdateHandler _telegramUpdates;
    private readonly TelegramOptions _telegram;
    private readonly IHostEnvironment _environment;

    public WebhooksController(LiveKitWebhookService liveKit, TelegramUpdateHandler telegramUpdates, IOptions<TelegramOptions> telegram, IHostEnvironment environment)
    {
        _liveKit = liveKit;
        _telegramUpdates = telegramUpdates;
        _telegram = telegram.Value;
        _environment = environment;
    }

    /// <summary>Заголовок, в котором Telegram повторяет секрет, заданный при установке webhook.</summary>
    public const string TelegramSecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    /// <summary>
    /// Обновления бота (команды <c>/start код</c> и <c>/stop</c>). Только вне Development (там long polling) и только с верным секретом в заголовке
    /// <c>X-Telegram-Bot-Api-Secret-Token</c> (401 <c>invalid_webhook_signature</c>). Без настроенного бота — 404.
    /// </summary>
    [HttpPost("telegram")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Telegram(CancellationToken ct)
    {
        if (_environment.IsDevelopment() || !_telegram.IsConfigured)
        {
            return NotFound();
        }

        var presented = Encoding.UTF8.GetBytes(Request.Headers[TelegramSecretHeader].ToString());
        var expected = Encoding.UTF8.GetBytes(_telegram.WebhookSecret);
        if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(presented, expected))
        {
            throw new UnauthorizedException("invalid_webhook_signature", "Секрет webhook неверный.");
        }

        var body = await ReadBodyAsync(ct);
        var update = TelegramBotClient.ParseUpdate(Encoding.UTF8.GetString(body))
            ?? throw new BadRequestException("invalid_webhook_payload", "Тело не похоже на обновление Telegram.");
        await _telegramUpdates.HandleAsync(update, ct);
        return Ok();
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
