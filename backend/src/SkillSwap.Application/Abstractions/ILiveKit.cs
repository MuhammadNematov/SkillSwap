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
}
