namespace SkillSwap.Domain.Enums;

/// <summary>Группы уведомлений для переключателей «Почта» и «Telegram».</summary>
public enum NotificationCategory
{
    /// <summary>Напоминания об уроке.</summary>
    LessonReminders,

    /// <summary>Урок отменён или перенесён.</summary>
    LessonChanges,

    /// <summary>Заявки на обмен: новая, принята, отклонена.</summary>
    ExchangeRequests,

    /// <summary>Новые сообщения в чате обмена.</summary>
    Messages,

    /// <summary>Обновления по моим обращениям.</summary>
    Cases,
}

public enum NotificationChannel
{
    Email,
    Telegram,
}
