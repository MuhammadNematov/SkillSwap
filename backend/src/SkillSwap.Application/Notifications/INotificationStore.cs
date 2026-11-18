using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Notifications;

public interface INotificationStore
{
    void Add(Notification notification);

    Task SaveAsync(CancellationToken ct);

    /// <summary>Уведомление для изменения (с отслеживанием).</summary>
    Task<Notification?> FindAsync(Guid id, CancellationToken ct);

    /// <summary>Уведомления пользователя, новые сверху, и общее число. Непрочитанное количество считается отдельно.</summary>
    Task<(IReadOnlyList<Notification> Items, int Total)> ListAsync(Guid userId, bool unreadOnly, int page, int pageSize, CancellationToken ct);

    Task<int> CountUnreadAsync(Guid userId, CancellationToken ct);

    /// <summary>Помечает все непрочитанные прочитанными, возвращает сколько.</summary>
    Task<int> MarkAllReadAsync(Guid userId, DateTime now, CancellationToken ct);
}

/// <summary>Доставка уведомления на открытые вкладки пользователя в реальном времени (хаб SignalR в Api).</summary>
public interface INotificationPusher
{
    Task PushAsync(Guid userId, NotificationDto notification, CancellationToken ct);
}
