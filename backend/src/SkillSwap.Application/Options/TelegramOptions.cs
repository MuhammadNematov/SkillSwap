namespace SkillSwap.Application.Options;

/// <summary>
/// Telegram-бот для уведомлений (секция <c>Telegram</c>). Токен — секрет: user-secrets или переменная <c>Telegram__BotToken</c>, в журналы он не попадает.
/// Без токена и имени бота функция выключена: приложение работает как обычно, блок на сайте скрыт.
/// </summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = string.Empty;

    /// <summary>Имя бота без «@»: из него строится ссылка <c>https://t.me/имя?start=код</c>.</summary>
    public string BotUsername { get; set; } = string.Empty;

    /// <summary>Секрет webhook (заголовок <c>X-Telegram-Bot-Api-Secret-Token</c>); обязателен вне Development. Латиница, цифры, «_», «-», 16–256 символов.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Публичный https-адрес API для webhook (без пути). Пусто — берётся <c>Frontend:BaseUrl</c> (когда API за тем же доменом).</summary>
    public string WebhookBaseUrl { get; set; } = string.Empty;

    /// <summary>Long polling в Development. Можно выключить, если бот уже опрашивается другим процессом.</summary>
    public bool Polling { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(BotUsername);

    public string BotName => BotUsername.Trim().TrimStart('@');
}
