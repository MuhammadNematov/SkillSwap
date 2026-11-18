using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Chat;

public interface IMessageStore
{
    void Add(Message message);

    void AddRevision(MessageRevision revision);

    /// <summary>Сообщение для изменения (с отслеживанием).</summary>
    Task<Message?> FindAsync(Guid id, CancellationToken ct);

    Task SaveAsync(CancellationToken ct);

    /// <summary>
    /// Страница истории: сообщения старше курсора (<paramref name="before"/>) от новых к старым, не больше <paramref name="limit"/>.
    /// Курсор — пара (время, id): сообщения с одинаковым временем не теряются и не повторяются.
    /// </summary>
    Task<IReadOnlyList<Message>> ListBeforeAsync(Guid exchangeId, (DateTime CreatedAt, Guid Id)? before, int limit, CancellationToken ct);

    /// <summary>Собеседник прочитал сообщения до указанного включительно (null — все). Возвращает, сколько отмечено.</summary>
    Task<int> MarkReadAsync(Guid exchangeId, Guid readerId, (DateTime CreatedAt, Guid Id)? upTo, DateTime now, CancellationToken ct);

    /// <summary>Сколько непрочитанных (чужих, не удалённых) сообщений в каждом из обменов.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountUnreadAsync(Guid userId, IReadOnlyCollection<Guid> exchangeIds, CancellationToken ct);
}

/// <summary>События чата для открытых вкладок участников (хаб SignalR в Api). Одна группа на обмен.</summary>
public interface IChatBroadcaster
{
    Task MessageCreatedAsync(MessageDto message, CancellationToken ct);

    Task MessageEditedAsync(MessageDto message, CancellationToken ct);

    Task MessageDeletedAsync(MessageDeletedDto deleted, CancellationToken ct);

    Task MessagesReadAsync(ReadReceiptDto receipt, CancellationToken ct);
}
