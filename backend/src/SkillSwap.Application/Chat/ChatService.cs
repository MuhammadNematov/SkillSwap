using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Chat;

/// <summary>
/// Чат обмена (раздел 7.1 ТЗ): отправка, правка и удаление в течение 15 минут, история с курсором, «прочитано», «печатает».
/// Доступ только у двух участников; чужой обмен и чужое сообщение выглядят как несуществующие (404). Писать можно, пока обмен активен;
/// в замороженном, завершённом и прерванном обмене чат только для чтения (после закрытия 30 дней, потом недоступен).
/// Текст шифруется Data Protection; проверка на антифрод идёт до шифрования.
/// </summary>
public sealed class ChatService
{
    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 100;

    private readonly IMessageStore _messages;
    private readonly Sessions.ILessonSessionStore _sessions;
    private readonly IExchangeStore _exchanges;
    private readonly IProfileStore _profiles;
    private readonly IMessageCipher _cipher;
    private readonly IChatBroadcaster _broadcaster;
    private readonly ChatThrottle _throttle;
    private readonly NewMessagesNotifier _newMessages;
    private readonly TimeProvider _clock;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        IMessageStore messages, Sessions.ILessonSessionStore sessions, IExchangeStore exchanges, IProfileStore profiles, IMessageCipher cipher, IChatBroadcaster broadcaster,
        ChatThrottle throttle, NewMessagesNotifier newMessages, TimeProvider clock, ILogger<ChatService> logger)
    {
        _messages = messages;
        _sessions = sessions;
        _exchanges = exchanges;
        _profiles = profiles;
        _cipher = cipher;
        _broadcaster = broadcaster;
        _throttle = throttle;
        _newMessages = newMessages;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    // ---------- отправка, правка, удаление ----------

    public async Task<MessageDto> SendAsync(Guid userId, Guid exchangeId, string? text, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var clean = CleanText(text);
        var exchange = await AccessAsync(userId, exchangeId, write: true, ct);
        _throttle.Hit(userId);

        var warning = PaymentContentDetector.LooksLikePayment(clean);   // до шифрования: потом текст сервер не читает
        var message = Message.CreateText(exchangeId, userId, _cipher.Encrypt(clean), warning);
        _messages.Add(message);
        await _messages.SaveAsync(ct);

        var dto = ToDto(message, clean);
        await PublishAsync(message, dto, exchange, userId, ct);
        return dto;
    }

    /// <summary>
    /// Проверка перед отправкой вложения (то же, что у текста): пользователь обычный и активный, обмен его, чат открыт на запись,
    /// не слишком часто. Возвращает обмен для <see cref="PublishAsync"/>.
    /// </summary>
    public async Task<ExchangeRow> AuthorizeSendAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var exchange = await AccessAsync(userId, exchangeId, write: true, ct);
        _throttle.Hit(userId);
        return exchange;
    }

    /// <summary>После сохранения нового сообщения: событие в группу обмена и уведомление собеседнику. Сбой рассылки отправку не ломает.</summary>
    public async Task PublishAsync(Message message, MessageDto dto, ExchangeRow exchange, Guid senderId, CancellationToken ct)
    {
        await TryBroadcastAsync(() => _broadcaster.MessageCreatedAsync(dto, ct));
        var sender = exchange.UserA.Id == senderId ? exchange.UserA : exchange.UserB;
        var recipient = exchange.UserA.Id == senderId ? exchange.UserB : exchange.UserA;
        await TryBroadcastAsync(() => _newMessages.OnMessageAsync(message, recipient.Id, senderId, sender.Name, ct));   // уведомление: сбой не ломает отправку
    }

    /// <summary>Сообщение для ответа клиенту и события (текст уже известен, вложение передаётся).</summary>
    public MessageDto ToMessageDto(Message message, Files.AttachmentDto? attachment, Sessions.LessonSessionDto? session = null) => ToDto(message, null, attachment, session);

    public async Task<MessageDto> EditAsync(Guid userId, Guid messageId, string? text, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var clean = CleanText(text);
        var message = await LoadOwnModifiableAsync(userId, messageId, ct);
        if (message.Type != MessageType.Text || message.EncryptedContent is null)
        {
            throw new ConflictException("message_not_editable", "Файл и голосовое отредактировать нельзя: удалите сообщение и отправьте заново.");
        }

        var previous = _cipher.Decrypt(message.EncryptedContent!);
        var changed = !string.Equals(previous, clean, StringComparison.Ordinal);
        var revision = message.Edit(userId, changed ? _cipher.Encrypt(clean) : message.EncryptedContent!, PaymentContentDetector.LooksLikePayment(clean), changed, Now());
        if (revision is null)
        {
            return ToDto(message, previous);   // текст тот же: правкой это не считается
        }

        _messages.AddRevision(revision);
        await _messages.SaveAsync(ct);

        var dto = ToDto(message, clean);
        await TryBroadcastAsync(() => _broadcaster.MessageEditedAsync(dto, ct));
        return dto;
    }

    public async Task<MessageDeletedDto> DeleteAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var message = await LoadOwnModifiableAsync(userId, messageId, ct);

        var revision = message.Delete(userId, Now());
        if (revision is not null)
        {
            _messages.AddRevision(revision);
        }

        await _messages.SaveAsync(ct);

        var deleted = new MessageDeletedDto(message.ExchangeId, message.Id, message.DeletedAt!.Value);
        await TryBroadcastAsync(() => _broadcaster.MessageDeletedAsync(deleted, ct));
        return deleted;
    }

    // ---------- история и «прочитано» ----------

    /// <param name="before">Курсор из <c>nextBefore</c> прошлой страницы; без него — самые новые.</param>
    public async Task<MessagePageDto> HistoryAsync(Guid userId, Guid exchangeId, string? before, int? limit, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        var size = limit ?? DefaultPageSize;
        if (size < 1 || size > MaxPageSize)
        {
            throw new BadRequestException("invalid_limit", $"limit должен быть от 1 до {MaxPageSize}.");
        }

        var cursor = before is null ? ((DateTime, Guid)?)null : DecodeCursor(before);
        var exchange = await AccessAsync(userId, exchangeId, write: false, ct);

        var page = await _messages.ListBeforeAsync(exchangeId, cursor, size + 1, ct);   // на одно больше: так видно, есть ли ещё
        var hasMore = page.Count > size;
        var items = page.Take(size).ToList();
        var nextBefore = hasMore ? EncodeCursor(items[^1]) : null;

        items.Reverse();   // внутри страницы старые сверху
        var attachments = await _messages.GetAttachmentsAsync(items.Where(m => m.AttachmentId is not null).Select(m => m.AttachmentId!.Value).ToList(), ct);
        var sessionIds = items.Where(m => m.SessionId is not null).Select(m => m.SessionId!.Value).ToList();
        var sessions = sessionIds.Count == 0 ? new Dictionary<Guid, LessonSession>() : (await _sessions.GetManyAsync(sessionIds, ct)).ToDictionary(p => p.Key, p => p.Value);
        return new MessagePageDto(items.Select(m => ToDto(
            m, null,
            m.AttachmentId is { } a && attachments.TryGetValue(a, out var att) ? Files.AttachmentMapper.ToDto(att) : null,
            m.SessionId is { } sid && sessions.TryGetValue(sid, out var ses) ? Sessions.LessonSessionService.ToDto(ses, exchange.UserA.Id, exchange.UserB.Id) : null)).ToList(), nextBefore);
    }

    /// <summary>Отметить прочитанными сообщения собеседника до указанного включительно (без указания — все). Читать можно и в режиме «только чтение».</summary>
    public async Task<ReadReceiptDto> MarkReadAsync(Guid userId, Guid exchangeId, Guid? upToMessageId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        await AccessAsync(userId, exchangeId, write: false, ct);

        (DateTime, Guid)? upTo = null;
        if (upToMessageId is { } id)
        {
            var target = await _messages.FindAsync(id, ct);
            if (target is null || target.ExchangeId != exchangeId)
            {
                throw new NotFoundException("message_not_found", "Сообщение не найдено.");
            }

            upTo = (target.CreatedAt, target.Id);
        }

        var now = Now();
        var marked = await _messages.MarkReadAsync(exchangeId, userId, upTo, now, ct);
        var receipt = new ReadReceiptDto(exchangeId, userId, upToMessageId, marked, now);
        await TryBroadcastAsync(() => OnReadAsync(userId, exchangeId, ct));
        if (marked > 0)
        {
            await TryBroadcastAsync(() => _broadcaster.MessagesReadAsync(receipt, ct));
        }

        return receipt;
    }

    /// <summary>Если непрочитанных не осталось, уведомление «новые сообщения» по обмену становится прочитанным.</summary>
    private async Task OnReadAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        var left = (await _messages.CountUnreadAsync(userId, [exchangeId], ct)).GetValueOrDefault(exchangeId);
        if (left == 0)
        {
            await _newMessages.OnReadAsync(userId, exchangeId, ct);
        }
    }

    // ---------- хаб: вход в группы и «печатает» ----------

    /// <summary>Можно ли пользователю слушать чат этого обмена (читать). Нет — 404 или 403 <c>chat_archived</c>.</summary>
    public async Task EnsureCanJoinAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        await AccessAsync(userId, exchangeId, write: false, ct);
    }

    /// <summary>«Печатает…»: разрешено, пока можно писать.</summary>
    public async Task EnsureCanTypeAsync(Guid userId, Guid exchangeId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        await AccessAsync(userId, exchangeId, write: true, ct);
    }

    /// <summary>Обмены, чаты которых пользователь видит: хаб подключает его к ним при входе.</summary>
    public async Task<IReadOnlyList<Guid>> ReadableExchangeIdsAsync(Guid userId, CancellationToken ct) =>
        await _exchanges.ListChatExchangeIdsAsync(userId, Now().AddDays(-ChatAccess.ReadOnlyDaysAfterClose), ct);

    // ---------- внутреннее ----------

    private async Task<ExchangeRow> AccessAsync(Guid userId, Guid exchangeId, bool write, CancellationToken ct)
    {
        var exchange = await _exchanges.GetViewAsync(exchangeId, ct);
        if (exchange is null || (exchange.UserA.Id != userId && exchange.UserB.Id != userId))
        {
            throw new NotFoundException("exchange_not_found", "Обмен не найден.");
        }

        var state = ChatAccess.Evaluate(exchange.Status, exchange.ClosedAt, Now());
        if (state.Mode == ChatMode.Archived)
        {
            throw new ForbiddenException("chat_archived", "Чат этого обмена больше недоступен: прошло более 30 дней после завершения.");
        }

        if (write && state.Mode != ChatMode.ReadWrite)
        {
            throw new ConflictException(
                "chat_read_only", "В этом обмене чат только для чтения.", new Dictionary<string, object?> { ["reason"] = state.ReadOnlyReason });
        }

        return exchange;
    }

    /// <summary>Своё, не удалённое, текстовое сообщение в обмене, где чат открыт на запись, в пределах 15 минут.</summary>
    private async Task<Message> LoadOwnModifiableAsync(Guid userId, Guid messageId, CancellationToken ct)
    {
        var message = await _messages.FindAsync(messageId, ct) ?? throw new NotFoundException("message_not_found", "Сообщение не найдено.");
        var exchange = await _exchanges.GetViewAsync(message.ExchangeId, ct);
        if (exchange is null || (exchange.UserA.Id != userId && exchange.UserB.Id != userId))
        {
            throw new NotFoundException("message_not_found", "Сообщение не найдено.");   // чужое сообщение неотличимо от несуществующего
        }

        await AccessAsync(userId, message.ExchangeId, write: true, ct);

        if (message.SenderId != userId)
        {
            throw new ForbiddenException("not_message_author", "Менять можно только своё сообщение.");
        }

        if (message.IsDeleted)
        {
            throw new ConflictException("message_deleted", "Сообщение уже удалено.");
        }

        if (!message.IsWithinEditWindow(Now()))
        {
            throw new ConflictException("edit_window_expired", "Править и удалять сообщение можно только в течение 15 минут после отправки.");
        }

        return message;
    }

    private static string CleanText(string? text)
    {
        var clean = text?.Trim();
        if (string.IsNullOrEmpty(clean))
        {
            throw new BadRequestException("message_empty", "Пустое сообщение.");
        }

        if (clean.Length > Message.TextMaxLength)
        {
            throw new BadRequestException("message_too_long", $"Сообщение длиннее {Message.TextMaxLength} символов.");
        }

        return clean;
    }

    private MessageDto ToDto(Message m, string? knownText, Files.AttachmentDto? attachment = null, Sessions.LessonSessionDto? session = null) => new(
        m.Id, m.ExchangeId, m.SenderId, m.Type,
        m.IsDeleted || m.EncryptedContent is null ? null : knownText ?? _cipher.Decrypt(m.EncryptedContent),
        m.PaymentWarning, m.AttachmentId, m.SessionId, m.CreatedAt, m.EditedAt, m.DeletedAt, m.ReadAt,
        m.IsDeleted ? null : attachment,   // у удалённого вложение не показывается
        session);

    private async Task TryBroadcastAsync(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сообщение уже сохранено: сбой рассылки не должен превращаться в ошибку для отправителя. Собеседник увидит его в истории.
            _logger.LogWarning(ex, "Не удалось разослать событие чата");
        }
    }

    private async Task RequireActiveRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Чат доступен только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }
    }

    // ---------- курсор истории: время и id последнего сообщения страницы ----------

    private static string EncodeCursor(Message m) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{m.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture)}:{m.Id:N}"));

    private static (DateTime CreatedAt, Guid Id) DecodeCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks is >= 0 and <= 3155378975999999999 && Guid.TryParseExact(parts[1], "N", out var id))
            {
                return (new DateTime(ticks, DateTimeKind.Utc), id);
            }
        }
        catch (FormatException)
        {
        }

        throw new BadRequestException("invalid_cursor", "Неверный курсор before: возьмите nextBefore из предыдущего ответа.");
    }
}
