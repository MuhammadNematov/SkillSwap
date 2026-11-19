using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Сообщение чата обмена (таблица Messages, разделы 7.1 и 13 ТЗ). Текст хранится только зашифрованным (Data Protection):
/// в дампе БД его не прочитать. Правка и удаление автором возможны в течение 15 минут; прежний текст сохраняется
/// в <see cref="MessageRevision"/> и виден модератору по обращению.
/// </summary>
public class Message : BaseEntity
{
    public const int TextMaxLength = 4000;

    /// <summary>Сколько после отправки автор может править и удалять сообщение (раздел 7.1 ТЗ).</summary>
    public static readonly TimeSpan EditWindow = TimeSpan.FromMinutes(15);

    private Message()
    {
    }

    public Guid ExchangeId { get; private set; }

    /// <summary>Автор; null у системных сообщений.</summary>
    public Guid? SenderId { get; private set; }

    public MessageType Type { get; private set; }

    /// <summary>Текст, зашифрованный Data Protection. У удалённого сообщения остаётся (скрыт от участников), чтобы модератор мог его увидеть.</summary>
    public string? EncryptedContent { get; private set; }

    public Guid? AttachmentId { get; private set; }
    public Guid? SessionId { get; private set; }

    /// <summary>В тексте найдены номер карты или призыв к оплате: участникам показывается предупреждение о запрете денежных расчётов.</summary>
    public bool PaymentWarning { get; private set; }

    public DateTime? EditedAt { get; private set; }
    public DateTime? DeletedAt { get; private set; }

    /// <summary>Когда собеседник прочитал сообщение (в обмене двое, поэтому одной отметки достаточно).</summary>
    public DateTime? ReadAt { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    /// <summary>Карточка урока в чате: текста нет, есть ссылка на урок. Карточку нельзя править и удалять: она показывает живое состояние урока.</summary>
    public static Message CreateSessionCard(Guid exchangeId, Guid senderId, Guid sessionId) =>
        new() { ExchangeId = exchangeId, SenderId = senderId, Type = MessageType.SessionCard, SessionId = sessionId };

    /// <summary>Сообщение с файлом или голосовым: текста нет, есть вложение.</summary>
    public static Message CreateAttachment(Guid exchangeId, Guid senderId, MessageType type, Guid attachmentId)
    {
        if (type is not (MessageType.File or MessageType.Voice))
        {
            throw new DomainValidationException("invalid_message_type", "Вложение бывает только у файла или голосового.");
        }

        return new Message { ExchangeId = exchangeId, SenderId = senderId, Type = type, AttachmentId = attachmentId };
    }

    public static Message CreateText(Guid exchangeId, Guid senderId, string encryptedContent, bool paymentWarning)
    {
        if (string.IsNullOrEmpty(encryptedContent))
        {
            throw new DomainValidationException("message_empty", "Пустое сообщение.");
        }

        return new Message
        {
            ExchangeId = exchangeId, SenderId = senderId, Type = MessageType.Text,
            EncryptedContent = encryptedContent, PaymentWarning = paymentWarning,
        };
    }

    public bool IsWithinEditWindow(DateTime now) => now - CreatedAt <= EditWindow;

    /// <summary>Автор правит текст. Возвращает запись о прежнем тексте; если текст не изменился, правки нет (null).</summary>
    public MessageRevision? Edit(Guid userId, string newEncryptedContent, bool paymentWarning, bool textChanged, DateTime now)
    {
        RequireModifiable(userId, now);
        if (!textChanged)
        {
            return null;
        }

        var revision = MessageRevision.Create(Id, EncryptedContent!, now);
        EncryptedContent = newEncryptedContent;
        PaymentWarning = paymentWarning;
        EditedAt = now;
        return revision;
    }

    /// <summary>
    /// Автор удаляет сообщение: участникам оно больше не показывается, а текст остаётся для модератора (запись о прежнем тексте;
    /// у вложения текста нет, файл остаётся в хранилище, записи нет).
    /// </summary>
    public MessageRevision? Delete(Guid userId, DateTime now)
    {
        RequireModifiable(userId, now, requireText: false);
        var revision = EncryptedContent is null ? null : MessageRevision.Create(Id, EncryptedContent, now);
        DeletedAt = now;
        return revision;
    }

    /// <summary>Собеседник прочитал. Повтор ничего не меняет.</summary>
    public void MarkRead(DateTime now) => ReadAt ??= now;

    private void RequireModifiable(Guid userId, DateTime now, bool requireText = true)
    {
        if (SenderId != userId)
        {
            throw new DomainValidationException("not_message_author", "Менять можно только своё сообщение.");
        }

        if (Type is MessageType.SessionCard or MessageType.System || (requireText && Type != MessageType.Text))
        {
            throw new DomainValidationException("message_not_editable", "Этот тип сообщений менять нельзя.");
        }

        if (IsDeleted)
        {
            throw new DomainValidationException("message_deleted", "Сообщение уже удалено.");
        }

        if (!IsWithinEditWindow(now))
        {
            throw new DomainValidationException("edit_window_expired", "Править и удалять сообщение можно только в течение 15 минут после отправки.");
        }
    }
}

/// <summary>Прежний текст сообщения до правки или удаления (таблица MessageRevisions): для модератора при споре.</summary>
public class MessageRevision : BaseEntity
{
    private MessageRevision()
    {
    }

    public Guid MessageId { get; private set; }
    public string PreviousEncryptedContent { get; private set; } = string.Empty;
    public DateTime ChangedAt { get; private set; }

    public static MessageRevision Create(Guid messageId, string previousEncryptedContent, DateTime changedAt) =>
        new() { MessageId = messageId, PreviousEncryptedContent = previousEncryptedContent, ChangedAt = changedAt };
}
