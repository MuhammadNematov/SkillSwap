using System.Text.Json;
using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Notifications;

/// <summary>Уведомления пользователя (раздел 12 ТЗ): список, «прочитано», «прочитать все». Чужое уведомление — 404.</summary>
public sealed class NotificationService
{
    private readonly INotificationStore _store;
    private readonly TimeProvider _clock;

    public NotificationService(INotificationStore store, TimeProvider clock)
    {
        _store = store;
        _clock = clock;
    }

    public static NotificationDto ToDto(Notification n)
    {
        using var document = JsonDocument.Parse(n.PayloadJson);
        return new NotificationDto(n.Id, n.Type, document.RootElement.Clone(), n.IsRead, n.CreatedAt, n.ReadAt);
    }

    public async Task<NotificationListDto> ListAsync(Guid userId, bool unreadOnly, int page, int pageSize, CancellationToken ct)
    {
        Paging.Validate(page, pageSize);
        var (items, total) = await _store.ListAsync(userId, unreadOnly, page, pageSize, ct);
        return new NotificationListDto(items.Select(ToDto).ToList(), page, pageSize, total, await _store.CountUnreadAsync(userId, ct));
    }

    public async Task<UnreadCountDto> UnreadCountAsync(Guid userId, CancellationToken ct) =>
        new(await _store.CountUnreadAsync(userId, ct));

    public async Task<NotificationDto> MarkReadAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var notification = await _store.FindAsync(id, ct);
        if (notification is null || notification.UserId != userId)
        {
            throw new NotFoundException("notification_not_found", "Уведомление не найдено.");
        }

        notification.MarkRead(_clock.GetUtcNow().UtcDateTime);
        await _store.SaveAsync(ct);
        return ToDto(notification);
    }

    public async Task<MarkedReadDto> MarkAllReadAsync(Guid userId, CancellationToken ct) =>
        new(await _store.MarkAllReadAsync(userId, _clock.GetUtcNow().UtcDateTime, ct));
}
