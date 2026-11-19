using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Abstractions;

/// <summary>Короткий токен для входа в комнату LiveKit.</summary>
public sealed record LiveKitToken(string Value, DateTime ExpiresAt);

/// <summary>Выдача токенов доступа в комнату LiveKit (раздел 7.2 ТЗ: «короткий токен, только участникам»).</summary>
public interface ILiveKitTokenService
{
    /// <summary>
    /// Токен на вход в комнату <paramref name="room"/> под именем <paramref name="identity"/>.
    /// Права: войти, публиковать звук и видео, слушать остальных, обмениваться данными.
    /// Кто и когда имеет право получить токен, решает вызывающий код: здесь только подпись.
    /// </summary>
    /// <exception cref="InvalidOperationException">LiveKit не настроен (нет секрета).</exception>
    LiveKitToken CreateJoinToken(string room, string identity, string? displayName);

    /// <summary>Короткий серверный токен (60 секунд) на удаление комнаты через API медиасервера. Участникам не выдаётся.</summary>
    string CreateRoomDeleteToken(string room);
}

/// <summary>Проверка подлинности webhook от LiveKit (раздел 10.2 ТЗ).</summary>
public interface ILiveKitWebhookVerifier
{
    /// <summary>
    /// true, только если заголовок Authorization содержит JWT, подписанный нашим API-секретом (HS256),
    /// от нашего ключа, не просроченный, и его claim sha256 совпадает с хешем тела запроса.
    /// Любая неопределённость (нет заголовка, не настроен секрет, мусор) даёт false.
    /// </summary>
    Task<bool> VerifyAsync(string? authorizationHeader, ReadOnlyMemory<byte> body);
}

/// <summary>Хранилище событий LiveKit с защитой от повторной доставки (раздел 11.1 ТЗ).</summary>
public interface ILiveKitWebhookEventStore
{
    /// <summary>
    /// Записывает событие. false, если событие с таким EventId уже есть (повторная доставка): тогда ничего не меняется.
    /// Запись и проверка уникальности — одна атомарная операция, поэтому параллельные доставки одного события не дублируются.
    /// </summary>
    Task<bool> TryAddAsync(LiveKitWebhookEvent webhookEvent, CancellationToken ct = default);

    /// <summary>Событие по его EventId, с отслеживанием (чтобы отметить обработанным).</summary>
    Task<LiveKitWebhookEvent?> FindAsync(string eventId, CancellationToken ct = default);

    /// <summary>Все события комнаты без отслеживания: из них собирается присутствие.</summary>
    Task<IReadOnlyList<LiveKitWebhookEvent>> ListByRoomAsync(string roomName, CancellationToken ct = default);

    /// <summary>Id событий комнаты, которые записаны, но ещё не учтены (ProcessedAt пусто), по порядку времени.</summary>
    Task<IReadOnlyList<string>> ListUnprocessedIdsAsync(string roomName, CancellationToken ct = default);

    Task SaveAsync(CancellationToken ct = default);
}

/// <summary>Учёт записанного события LiveKit в посещаемости уроков (раздел 6.3 ТЗ). Повторный вызов для обработанного события безвреден.</summary>
public interface ILiveKitEventProcessor
{
    Task ProcessAsync(string eventId, CancellationToken ct);
}

/// <summary>Серверные вызовы медиасервера LiveKit (раздел 7.2 ТЗ: при закрытии окна бэкенд удаляет комнату).</summary>
public interface ILiveKitRoomService
{
    /// <summary>Настроено ли всё нужное (адрес, ключ и секрет). Если нет, удалять нечего: комнат не было.</summary>
    bool IsConfigured { get; }

    /// <summary>Удаляет комнату и отключает всех, кто в ней. Комнаты нет — не ошибка (её могли удалить или она закрылась сама).</summary>
    /// <exception cref="LiveKitUnavailableException">Медиасервер недоступен или ответил ошибкой: вызывающий повторит позже.</exception>
    Task DeleteRoomAsync(string room, CancellationToken ct);
}

public sealed class LiveKitUnavailableException : Exception
{
    public LiveKitUnavailableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
