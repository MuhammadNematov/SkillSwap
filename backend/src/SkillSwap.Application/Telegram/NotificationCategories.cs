using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Telegram;

/// <summary>Какие уведомления в какую группу переключателей попадают.</summary>
public static class NotificationCategories
{
    /// <summary>Уведомления интерфейса, которые уходят в Telegram (напоминания об уроке идут отдельными задачами за 1 час и 10 минут, сообщения — отдельным путём).</summary>
    public static NotificationCategory? ForTelegram(NotificationType type) => type switch
    {
        NotificationType.RequestReceived or NotificationType.RequestAccepted or NotificationType.RequestDeclined => NotificationCategory.ExchangeRequests,
        NotificationType.SessionCancelled or NotificationType.SessionProposed => NotificationCategory.LessonChanges,   // «предложено» — только перенос (см. шаблон)
        NotificationType.CaseTaken or NotificationType.CaseReturnedToQueue or NotificationType.CaseExplanationRequested or NotificationType.CaseExplanationReminder
            or NotificationType.CaseResolved or NotificationType.CaseAppealed or NotificationType.CaseAppealReviewAssigned or NotificationType.AppealDecided => NotificationCategory.Cases,
        _ => null,
    };

    /// <summary>Группа письма-дубля уведомления; null — письмо не отключается переключателем.</summary>
    public static NotificationCategory? ForEmail(NotificationType type) => type switch
    {
        NotificationType.RequestReceived or NotificationType.RequestAccepted => NotificationCategory.ExchangeRequests,
        NotificationType.SessionReminder => NotificationCategory.LessonReminders,
        NotificationType.CaseResolved or NotificationType.AppealDecided => NotificationCategory.Cases,
        _ => null,
    };

    /// <summary>Есть ли письма этой группы: об отмене и переносе урока письма не отправляются.</summary>
    public static bool HasEmail(NotificationCategory category) => category != NotificationCategory.LessonChanges;
}
