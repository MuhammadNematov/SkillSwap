using System.Text.Json;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Notifications;

/// <param name="Payload">Данные для текста (ids, имена); состав зависит от <c>type</c>, см. docs/BACKEND-DEV.md.</param>
public sealed record NotificationDto(Guid Id, NotificationType Type, JsonElement Payload, bool IsRead, DateTime CreatedAt, DateTime? ReadAt);

/// <summary>Страница уведомлений и общее число непрочитанных (для значка).</summary>
public sealed record NotificationListDto(IReadOnlyList<NotificationDto> Items, int Page, int PageSize, int TotalCount, int UnreadCount);

public sealed record MarkedReadDto(int Marked);

public sealed record UnreadCountDto(int UnreadCount);
