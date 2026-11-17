using System.ComponentModel.DataAnnotations;

namespace SkillSwap.Application.Options;

/// <summary>
/// Медиасервер LiveKit, секция "LiveKit". Ключ и секрет — те же, что у самого LiveKit-сервера
/// (в локальной разработке — <c>LIVEKIT_KEYS</c> в docker-compose.yml).
/// <para>
/// ApiSecret — секрет: только user-secrets или переменная окружения <c>LiveKit__ApiSecret</c>, не в репозитории.
/// Пока он не задан, выдача токенов и приём webhook выключены (webhook отвечает 401: проверить подпись нечем).
/// </para>
/// </summary>
public sealed class LiveKitOptions
{
    public const string SectionName = "LiveKit";

    /// <summary>Не короче: токены подписываются HS256, короткий секрет слабый (и сам IdentityModel такой не примет).</summary>
    public const int MinSecretLength = 32;

    /// <summary>Адрес для браузера (WebSocket). В разработке — контейнер из docker-compose.yml.</summary>
    [Required]
    public string Url { get; set; } = "ws://localhost:7880";

    /// <summary>Имя ключа: в токене это издатель (iss), в webhook подпись проверяется по нему.</summary>
    [Required]
    public string ApiKey { get; set; } = "devkey";

    public string? ApiSecret { get; set; }

    /// <summary>Сколько токен действует до входа в комнату. Токен короткий (раздел 7.2 ТЗ): после входа сеанс от него не зависит.</summary>
    [Range(30, 3600)]
    public int TokenTtlSeconds { get; set; } = 300;

    /// <summary>Есть ли всё нужное, чтобы подписывать токены и проверять webhook.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) && ApiSecret is { Length: >= MinSecretLength };
}
