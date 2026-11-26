using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Options;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Telegram;

/// <summary>Переключатели «какие уведомления присылать» по группам и каналам. По умолчанию всё включено.</summary>
public sealed class NotificationSettingsService
{
    private readonly ITelegramStore _store;
    private readonly IProfileStore _profiles;
    private readonly IUnitOfWork _uow;
    private readonly TelegramOptions _options;

    public NotificationSettingsService(ITelegramStore store, IProfileStore profiles, IUnitOfWork uow, IOptions<TelegramOptions> options)
    {
        _store = store;
        _profiles = profiles;
        _uow = uow;
        _options = options.Value;
    }

    public async Task<NotificationSettingsDto> GetAsync(Guid userId, CancellationToken ct)
    {
        await RequireRegularAsync(userId, ct);
        var saved = (await _store.ListPreferencesAsync(userId, ct)).ToDictionary(p => (p.Category, p.Channel), p => p.Enabled);
        var items = Enum.GetValues<NotificationCategory>().Select(c => new NotificationSettingDto(
            c,
            NotificationCategories.HasEmail(c) ? saved.GetValueOrDefault((c, NotificationChannel.Email), true) : null,
            saved.GetValueOrDefault((c, NotificationChannel.Telegram), true))).ToList();
        var connected = _options.IsConfigured && await _store.FindByUserAsync(userId, ct) is not null;
        return new NotificationSettingsDto(_options.IsConfigured, connected, items);
    }

    public async Task<NotificationSettingsDto> UpdateAsync(Guid userId, UpdateNotificationSettingsRequest request, CancellationToken ct)
    {
        await RequireRegularAsync(userId, ct);
        var updates = request.Items ?? [];
        if (updates.Count == 0 || updates.Any(u => !Enum.IsDefined(u.Category)))
        {
            throw new BadRequestException("invalid_notification_settings", "Укажите хотя бы одну настройку из списка.");
        }

        if (updates.Any(u => u.Email is not null && !NotificationCategories.HasEmail(u.Category)))
        {
            throw new BadRequestException("invalid_notification_settings", "Писем этой группы нет.");
        }

        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([userId], token);
            var existing = (await _store.ListPreferencesAsync(userId, token)).ToDictionary(p => (p.Category, p.Channel));
            foreach (var update in updates)
            {
                Set(update.Category, NotificationChannel.Email, update.Email);
                Set(update.Category, NotificationChannel.Telegram, update.Telegram);
            }

            await _store.SaveAsync(token);
            return 0;

            void Set(NotificationCategory category, NotificationChannel channel, bool? value)
            {
                if (value is not { } enabled)
                {
                    return;
                }

                if (existing.TryGetValue((category, channel), out var row))
                {
                    row.Set(enabled);
                }
                else
                {
                    var created = NotificationPreference.Create(userId, category, channel, enabled);
                    existing[(category, channel)] = created;
                    _store.Add(created);
                }
            }
        }, ct);

        return await GetAsync(userId, ct);
    }

    private async Task RequireRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Настройки уведомлений есть у обычных аккаунтов.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }
    }
}
