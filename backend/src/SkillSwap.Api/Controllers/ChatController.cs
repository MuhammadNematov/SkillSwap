using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SkillSwap.Application.Chat;

namespace SkillSwap.Api.Controllers;

/// <summary>
/// История чата и «прочитано» (раздел 7.1 ТЗ). Отправка, правка, удаление и «печатает» идут через хаб <c>/hubs/chat</c>.
/// Чужой обмен — 404 <c>exchange_not_found</c>; через 30 дней после завершения чат недоступен — 403 <c>chat_archived</c>.
/// </summary>
[ApiController]
[Route("api/v1/exchanges/{exchangeId:guid}/messages")]
[Authorize]
[Tags("Chat")]
public class ChatController : ControllerBase
{
    private readonly ChatService _chat;

    public ChatController(ChatService chat)
    {
        _chat = chat;
    }

    /// <summary>
    /// История с курсором. Без <c>before</c> — самые новые сообщения; с <c>before</c> (взять из <c>nextBefore</c> прошлого ответа) — старше.
    /// <c>limit</c> 1–100 (по умолчанию 30). <c>items</c> по возрастанию времени, <c>nextBefore</c> = null, когда история закончилась.
    /// Удалённые сообщения приходят без текста (<c>deletedAt</c>). Ошибки: 400 <c>invalid_cursor</c>, <c>invalid_limit</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<MessagePageDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> History(Guid exchangeId, [FromQuery] string? before, [FromQuery] int? limit, CancellationToken ct) =>
        Ok(await _chat.HistoryAsync(UserId(), exchangeId, before, limit, ct));

    /// <summary>
    /// Отметить сообщения собеседника прочитанными: до <c>upToMessageId</c> включительно, без тела — все. Работает и когда чат только для чтения.
    /// Собеседнику уходит событие <c>messagesRead</c>. Повтор безвреден (<c>marked: 0</c>).
    /// </summary>
    [HttpPost("read")]
    [ProducesResponseType<ReadReceiptDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Read(Guid exchangeId, [FromBody] MarkReadRequest? request, CancellationToken ct) =>
        Ok(await _chat.MarkReadAsync(UserId(), exchangeId, request?.UpToMessageId, ct));

    private Guid UserId() => Guid.Parse(User.FindFirstValue("sub")!);
}
