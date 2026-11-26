using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Telegram;

/// <param name="Enabled">Настроен ли бот на сервере. false — блок на сайте скрывается.</param>
public sealed record TelegramStatusDto(bool Enabled, bool Connected, string? BotUsername, DateTime? LinkedAt);

/// <param name="Url">Ссылка <c>https://t.me/бот?start=код</c>: открыть и нажать «Start». Код одноразовый и действует до <paramref name="ExpiresAt"/>.</param>
public sealed record TelegramLinkDto(string Url, DateTime ExpiresAt);

/// <param name="Email">Присылать письма; null — писем этой группы нет, переключателя нет.</param>
public sealed record NotificationSettingDto(NotificationCategory Category, bool? Email, bool Telegram);

public sealed record NotificationSettingsDto(bool TelegramEnabled, bool TelegramConnected, IReadOnlyList<NotificationSettingDto> Items);

public sealed record NotificationSettingUpdate(NotificationCategory Category, bool? Email, bool? Telegram);

public sealed record UpdateNotificationSettingsRequest(IReadOnlyList<NotificationSettingUpdate>? Items);
