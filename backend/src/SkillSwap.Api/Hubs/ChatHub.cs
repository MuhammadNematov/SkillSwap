using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Exceptions;

namespace SkillSwap.Api.Hubs;

/// <summary>
/// Хаб чата <c>/hubs/chat</c> (раздел 7.1 ТЗ): одна группа на обмен. Токен: заголовок <c>Authorization: Bearer</c> или параметр
/// <c>access_token</c> (браузерный WebSocket). При подключении сервер сам добавляет пользователя в группы всех его обменов, чат
/// которых виден (после переподключения ничего повторять не нужно); для обмена, созданного уже после подключения, вызовите <c>Join</c>.
///
/// Методы клиента: <c>Join(exchangeId)</c>, <c>Leave(exchangeId)</c>, <c>Send(exchangeId, text)</c> → сообщение, <c>Edit(messageId, text)</c> → сообщение,
/// <c>Delete(messageId)</c>, <c>Typing(exchangeId)</c>, <c>Read(exchangeId, upToMessageId)</c> (null — все). Ошибка приходит как исключение
/// с машинным кодом в тексте: <c>chat_read_only</c>, <c>chat_archived</c>, <c>exchange_not_found</c>, <c>message_not_found</c>, <c>not_message_author</c>,
/// <c>edit_window_expired</c>, <c>message_deleted</c>, <c>message_empty</c>, <c>message_too_long</c>, <c>chat_rate_limited</c> и др.
///
/// События от сервера (всем в группе обмена, включая другие вкладки автора): <c>message</c> (сообщение), <c>messageEdited</c> (сообщение),
/// <c>messageDeleted</c> ({ exchangeId, messageId, deletedAt }), <c>messagesRead</c> ({ exchangeId, readerId, upToMessageId, marked, readAt }),
/// <c>typing</c> ({ exchangeId, userId }, только собеседнику), <c>sessionUpdated</c> (урок целиком: карточка урока в чате обновляется у обоих).
/// </summary>
[Authorize]
public sealed class ChatHub : Hub
{
    public const string MessageEvent = "message";
    public const string MessageEditedEvent = "messageEdited";
    public const string MessageDeletedEvent = "messageDeleted";
    public const string MessagesReadEvent = "messagesRead";
    public const string TypingEvent = "typing";
    public const string SessionUpdatedEvent = "sessionUpdated";

    /// <summary>«Печатает…» не чаще раза в 2 секунды с одного соединения: клиенты шлют его на каждое нажатие клавиши.</summary>
    private static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, DateTime> LastTyping = new();

    private readonly ChatService _chat;
    private readonly TimeProvider _clock;
    private readonly ConnectionRegistry _registry;
    private readonly SkillSwap.Application.Profile.IProfileStore _profiles;

    public ChatHub(ChatService chat, TimeProvider clock, ConnectionRegistry registry, SkillSwap.Application.Profile.IProfileStore profiles)
    {
        _chat = chat;
        _clock = clock;
        _registry = registry;
        _profiles = profiles;
    }

    public static string GroupName(Guid exchangeId) => $"exchange:{exchangeId:N}";

    private Guid UserId => Guid.Parse(Context.User!.FindFirstValue("sub")!);

    public override async Task OnConnectedAsync()
    {
        if (!await _registry.AcceptAsync(Context, _profiles))
        {
            return;   // аккаунт недействующий (заблокирован): соединение оборвано
        }

        foreach (var exchangeId in await _chat.ReadableExchangeIdsAsync(UserId, Context.ConnectionAborted))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(exchangeId), Context.ConnectionAborted);
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.Remove(Context.ConnectionId);
        foreach (var key in LastTyping.Keys.Where(k => k.StartsWith(Context.ConnectionId + "|", StringComparison.Ordinal)))
        {
            LastTyping.TryRemove(key, out _);
        }

        return base.OnDisconnectedAsync(exception);
    }

    public Task Join(Guid exchangeId) => Run(async () =>
    {
        await _chat.EnsureCanJoinAsync(UserId, exchangeId, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(exchangeId), Context.ConnectionAborted);
    });

    public Task Leave(Guid exchangeId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(exchangeId), Context.ConnectionAborted);

    public Task<MessageDto> Send(Guid exchangeId, string? text) => Run(() => _chat.SendAsync(UserId, exchangeId, text, Context.ConnectionAborted));

    public Task<MessageDto> Edit(Guid messageId, string? text) => Run(() => _chat.EditAsync(UserId, messageId, text, Context.ConnectionAborted));

    public Task<MessageDeletedDto> Delete(Guid messageId) => Run(() => _chat.DeleteAsync(UserId, messageId, Context.ConnectionAborted));

    public Task<ReadReceiptDto> Read(Guid exchangeId, Guid? upToMessageId) =>
        Run(() => _chat.MarkReadAsync(UserId, exchangeId, upToMessageId, Context.ConnectionAborted));

    public Task Typing(Guid exchangeId) => Run(async () =>
    {
        await _chat.EnsureCanTypeAsync(UserId, exchangeId, Context.ConnectionAborted);

        var now = _clock.GetUtcNow().UtcDateTime;
        var key = $"{Context.ConnectionId}|{exchangeId:N}";
        if (LastTyping.TryGetValue(key, out var last) && now - last < TypingInterval)
        {
            return;
        }

        LastTyping[key] = now;
        await Clients.OthersInGroup(GroupName(exchangeId)).SendAsync(TypingEvent, new TypingDto(exchangeId, UserId), Context.ConnectionAborted);
    });

    /// <summary>Ожидаемые ошибки сервисов превращаются в исключение хаба с машинным кодом: клиент получает только его, без внутренностей.</summary>
    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (AppException ex)
        {
            throw new HubException(ex.Code);
        }
        catch (Domain.Exceptions.DomainException ex)
        {
            throw new HubException(ex.Code);
        }
    }

    private static async Task Run(Func<Task> action) => await Run(async () =>
    {
        await action();
        return 0;
    });
}

/// <summary>Рассылка событий чата в группу обмена.</summary>
public sealed class SignalRChatBroadcaster : IChatBroadcaster
{
    private readonly IHubContext<ChatHub> _hub;
    private readonly SkillSwap.Application.Sessions.ISessionAttendanceStore _attendance;

    public SignalRChatBroadcaster(IHubContext<ChatHub> hub, SkillSwap.Application.Sessions.ISessionAttendanceStore attendance)
    {
        _hub = hub;
        _attendance = attendance;
    }

    public Task MessageCreatedAsync(MessageDto message, CancellationToken ct) =>
        _hub.Clients.Group(ChatHub.GroupName(message.ExchangeId)).SendAsync(ChatHub.MessageEvent, message, ct);

    public Task MessageEditedAsync(MessageDto message, CancellationToken ct) =>
        _hub.Clients.Group(ChatHub.GroupName(message.ExchangeId)).SendAsync(ChatHub.MessageEditedEvent, message, ct);

    public Task MessageDeletedAsync(MessageDeletedDto deleted, CancellationToken ct) =>
        _hub.Clients.Group(ChatHub.GroupName(deleted.ExchangeId)).SendAsync(ChatHub.MessageDeletedEvent, deleted, ct);

    /// <summary>Урок уходит вместе с тем, кто сейчас в комнате (<c>participantsInRoom</c>): так фронтенд показывает «партнёр в комнате».</summary>
    public async Task SessionUpdatedAsync(SkillSwap.Application.Sessions.LessonSessionDto session, CancellationToken ct)
    {
        var inRoom = (await _attendance.ListInRoomAsync([session.Id], ct)).GetValueOrDefault(session.Id) ?? [];
        await _hub.Clients.Group(ChatHub.GroupName(session.ExchangeId)).SendAsync(ChatHub.SessionUpdatedEvent, session with { ParticipantsInRoom = inRoom }, ct);
    }

    public Task MessagesReadAsync(ReadReceiptDto receipt, CancellationToken ct) =>
        _hub.Clients.Group(ChatHub.GroupName(receipt.ExchangeId)).SendAsync(ChatHub.MessagesReadEvent, receipt, ct);
}
