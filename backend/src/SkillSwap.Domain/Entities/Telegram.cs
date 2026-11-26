using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Domain.Entities;

/// <summary>Связь аккаунта с чатом Telegram: у одного аккаунта один чат, у одного чата один аккаунт (уникальные индексы).</summary>
public class TelegramLink : BaseEntity
{
    private TelegramLink()
    {
    }

    public Guid UserId { get; private set; }
    public long ChatId { get; private set; }
    public DateTime LinkedAt { get; private set; }

    public static TelegramLink Create(Guid userId, long chatId, DateTime now) => new() { UserId = userId, ChatId = chatId, LinkedAt = now };
}

/// <summary>
/// Одноразовый код подключения (deep link <c>t.me/бот?start=код</c>). В базе лежит только SHA-256 кода; код живёт <see cref="Lifetime"/> и принимается один раз.
/// </summary>
public class TelegramLinkCode : BaseEntity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private TelegramLinkCode()
    {
    }

    public Guid UserId { get; private set; }
    public string CodeHash { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public DateTime? UsedAt { get; private set; }
    public long? UsedByChatId { get; private set; }

    public static TelegramLinkCode Create(Guid userId, string codeHash, DateTime now) => new() { UserId = userId, CodeHash = codeHash, ExpiresAt = now + Lifetime };

    public bool IsUsable(DateTime now) => UsedAt is null && now < ExpiresAt;

    public void Use(long chatId, DateTime now)
    {
        UsedAt = now;
        UsedByChatId = chatId;
    }
}

/// <summary>Выбор пользователя: присылать ли уведомления группы <see cref="Category"/> по каналу <see cref="Channel"/>. Нет строки — включено.</summary>
public class NotificationPreference : BaseEntity
{
    private NotificationPreference()
    {
    }

    public Guid UserId { get; private set; }
    public NotificationCategory Category { get; private set; }
    public NotificationChannel Channel { get; private set; }
    public bool Enabled { get; private set; }

    public static NotificationPreference Create(Guid userId, NotificationCategory category, NotificationChannel channel, bool enabled) =>
        new() { UserId = userId, Category = category, Channel = channel, Enabled = enabled };

    public void Set(bool enabled) => Enabled = enabled;
}

/// <summary>Когда по этому диалогу получателю в последний раз ушло сообщение «Новое сообщение» в Telegram (не чаще раза в 5 минут).</summary>
public class TelegramDialogThrottle
{
    public Guid UserId { get; set; }
    public Guid ExchangeId { get; set; }
    public DateTime LastSentAt { get; set; }
}
