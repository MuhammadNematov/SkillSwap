using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Telegram;

namespace SkillSwap.Api.Hubs;

/// <summary>
/// Хаб уведомлений <c>/hubs/notifications</c> (раздел 7.1 ТЗ). Подключение с JWT: заголовок <c>Authorization: Bearer</c> или
/// (для WebSocket в браузере) параметр <c>access_token</c> в адресе. Сервер шлёт клиенту событие <c>notification</c> с объектом
/// уведомления (как в <c>GET /notifications</c>); клиент ничего не вызывает. Каждый пользователь получает только свои.
/// </summary>
[Authorize]
public sealed class NotificationsHub : Hub
{
    /// <summary>Имя события, которое получает клиент.</summary>
    public const string EventName = "notification";

    private readonly ConnectionRegistry _registry;
    private readonly IProfileStore _profiles;

    public NotificationsHub(ConnectionRegistry registry, IProfileStore profiles)
    {
        _registry = registry;
        _profiles = profiles;
    }

    public override async Task OnConnectedAsync()
    {
        if (await _registry.AcceptAsync(Context, _profiles))
        {
            await base.OnConnectedAsync();
        }
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Пользователь SignalR — клейм <c>sub</c> (клеймы в приложении не переименовываются, стандартный провайдер их не находит).</summary>
public sealed class SubUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User.FindFirst("sub")?.Value;
}

/// <summary>Доставка уведомления на все открытые вкладки пользователя.</summary>
public sealed class SignalRNotificationPusher : INotificationPusher
{
    private readonly IHubContext<NotificationsHub> _hub;

    public SignalRNotificationPusher(IHubContext<NotificationsHub> hub)
    {
        _hub = hub;
    }

    public Task PushAsync(Guid userId, NotificationDto notification, CancellationToken ct) =>
        _hub.Clients.User(userId.ToString()).SendAsync(NotificationsHub.EventName, notification, ct);
}

/// <summary>Сообщает открытым вкладкам, что Telegram подключён или отключён (событие <c>telegramStatus</c> того же хаба).</summary>
public sealed class SignalRTelegramStatusPusher : ITelegramStatusPusher
{
    public const string EventName = "telegramStatus";

    private readonly IHubContext<NotificationsHub> _hub;

    public SignalRTelegramStatusPusher(IHubContext<NotificationsHub> hub)
    {
        _hub = hub;
    }

    public Task PushAsync(Guid userId, bool connected, CancellationToken ct) =>
        _hub.Clients.User(userId.ToString()).SendAsync(EventName, new { connected }, ct);
}
