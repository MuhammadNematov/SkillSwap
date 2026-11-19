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

    /// <summary>Непрочитанное уведомление «новые сообщения» по обмену (с отслеживанием) или null.</summary>
    Task<Notification?> FindUnreadNewMessagesAsync(Guid userId, Guid exchangeId, CancellationToken ct);

    /// <summary>Самое свежее уведомление «новые сообщения» по обмену, прочитанное или нет (с отслеживанием).</summary>
    Task<Notification?> FindLatestNewMessagesAsync(Guid userId, Guid exchangeId, CancellationToken ct);

    /// <summary>Когда по этому обмену в последний раз отправлялось письмо о непрочитанных сообщениях.</summary>
    Task<DateTime?> LastNewMessagesEmailAtAsync(Guid userId, Guid exchangeId, CancellationToken ct);

    /// <summary>Собеседник прочитал чат: уведомление по обмену становится прочитанным. Возвращает, сколько отмечено (0 или 1).</summary>
    Task<int> MarkNewMessagesReadAsync(Guid userId, Guid exchangeId, DateTime now, CancellationToken ct);

    /// <summary>Помечает все непрочитанные прочитанными, возвращает сколько.</summary>
    Task<int> MarkAllReadAsync(Guid userId, DateTime now, CancellationToken ct);
}

/// <summary>Доставка уведомления на открытые вкладки пользователя в реальном времени (хаб SignalR в Api).</summary>
public interface INotificationPusher
{
    Task PushAsync(Guid userId, NotificationDto notification, CancellationToken ct);
}
