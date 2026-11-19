namespace SkillSwap.Application.Options;

/// <summary>
/// Хранилище файлов по протоколу S3 (секция "Storage"): в разработке и на сервере Garage (docker-compose), позже можно перейти на любое
/// облачное S3 только сменой настроек. Бакет закрытый: файлы отдаются подписанными ссылками (5 минут) или через приложение (аватары).
/// Ключи доступа только в переменных окружения или user-secrets (<c>Storage__AccessKey</c>, <c>Storage__SecretKey</c>).
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Адрес S3 API, с которого приложение ходит само (например, http://localhost:3900). Пусто — хранилище не настроено.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Адрес, по которому хранилище видит браузер, если он отличается от <see cref="Endpoint"/> (приложение и браузер в разных сетях).
    /// Подписанные ссылки строятся на него: подпись зависит от адреса. Пусто — тот же, что <see cref="Endpoint"/>.
    /// </summary>
    public string? PublicEndpoint { get; set; }

    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string Bucket { get; set; } = "skillswap";

    /// <summary>Регион: у Garage <c>garage</c>, у облачного S3 настоящий регион (например, eu-central-1).</summary>
    public string Region { get; set; } = "garage";

    /// <summary>Адрес вида endpoint/bucket/key (нужен Garage и большинству самохостных решений); у облачного S3 можно выключить.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Сколько секунд ждать ответа хранилища (по умолчанию 30). В тестах «хранилище недоступно» ставится короче.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Сколько раз повторять запрос к хранилищу при сбое сети или 5xx (по умолчанию 2). 0 — без повторов.</summary>
    public int MaxRetries { get; set; } = 2;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);
}
