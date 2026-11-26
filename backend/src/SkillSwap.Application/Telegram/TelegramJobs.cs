using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Options;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Sessions;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Telegram;

/// <summary>
/// Фоновые задачи «что отправить»: проверяют, подключён ли Telegram, включена ли группа, нужно ли ещё отправлять, и ставят доставку
/// (<see cref="TelegramDeliveryJobs"/>). Каждая идемпотентна и проверяет текущее состояние.
/// </summary>
public sealed class TelegramJobs
{
    /// <summary>«Новое сообщение» по одному диалогу не чаще раза в это время.</summary>
    public static readonly TimeSpan MessageInterval = TimeSpan.FromMinutes(5);

    /// <summary>За сколько минут до урока напоминать в Telegram.</summary>
    public static readonly int[] ReminderMinutes = [60, 10];

    private readonly INotificationStore _notifications;
    private readonly IProfileStore _profiles;
    private readonly ITelegramStore _store;
    private readonly IUserPresence _presence;
    private readonly ILessonSessionStore _sessions;
    private readonly IExchangeStore _exchanges;
    private readonly IUnitOfWork _uow;
    private readonly IBackgroundJobs _jobs;
    private readonly FrontendOptions _frontend;
    private readonly TelegramOptions _options;
    private readonly TimeProvider _clock;

    public TelegramJobs(
        INotificationStore notifications, IProfileStore profiles, ITelegramStore store, IUserPresence presence, ILessonSessionStore sessions,
        IExchangeStore exchanges, IUnitOfWork uow, IBackgroundJobs jobs, IOptions<FrontendOptions> frontend, IOptions<TelegramOptions> options, TimeProvider clock)
    {
        _notifications = notifications;
        _profiles = profiles;
        _store = store;
        _presence = presence;
        _sessions = sessions;
        _exchanges = exchanges;
        _uow = uow;
        _jobs = jobs;
        _frontend = frontend.Value;
        _options = options.Value;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Уведомление интерфейса (заявки, отмена и перенос урока, обращения) — в Telegram, один раз.</summary>
    public async Task NotificationAsync(Guid notificationId)
    {
        var ct = CancellationToken.None;
        var notification = await _notifications.FindAsync(notificationId, ct);
        if (notification is null || notification.TelegramQueuedAt is not null || NotificationCategories.ForTelegram(notification.Type) is not { } category)
        {
            return;
        }

        if (await RecipientAsync(notification.UserId, category, ct) is not { } user)
        {
            return;
        }

        using var payload = JsonDocument.Parse(notification.PayloadJson);
        if (TelegramTexts.ForNotification(notification.Type, user.UiLanguage, payload.RootElement, _frontend, user.TimeZone) is not { } message)
        {
            return;
        }

        notification.MarkTelegramQueued(Now());
        await _notifications.SaveAsync(ct);
        Deliver(user.Id, message);
    }

    /// <summary>
    /// Новое сообщение в чате: только «Новое сообщение от имя» без текста. Не отправляется, пока получатель на сайте, и не чаще раза в 5 минут на диалог.
    /// </summary>
    public async Task NewMessageAsync(Guid recipientId, Guid exchangeId, Guid senderId)
    {
        var ct = CancellationToken.None;
        if (_presence.IsOnline(recipientId) || await RecipientAsync(recipientId, NotificationCategory.Messages, ct) is not { } user)
        {
            return;
        }

        var exchange = await _exchanges.GetViewAsync(exchangeId, ct);
        if (exchange is null || (exchange.UserA.Id != recipientId && exchange.UserB.Id != recipientId))
        {
            return;
        }

        var sender = exchange.UserA.Id == senderId ? exchange.UserA : exchange.UserB;
        if (!await _store.TryAcquireDialogSlotAsync(recipientId, exchangeId, Now(), MessageInterval, ct))
        {
            return;
        }

        Deliver(user.Id, TelegramTexts.NewMessage(user.UiLanguage, sender.Name, _frontend.ExchangeUrl(exchangeId)));
    }

    /// <summary>Напоминание за 1 час или за 10 минут: обоим участникам, если урок всё ещё запланирован на то же время.</summary>
    public async Task ReminderAsync(Guid sessionId, long startAtTicks, int minutesBefore)
    {
        var ct = CancellationToken.None;
        var peek = (await _sessions.GetManyAsync([sessionId], ct)).GetValueOrDefault(sessionId);
        if (peek is null || !peek.StartsAt(startAtTicks))
        {
            return;   // урока нет или он перенесён: задача на старое время ничего не делает
        }

        var exchange = await _exchanges.GetViewAsync(peek.ExchangeId, ct);
        if (exchange is null)
        {
            return;
        }

        var send = await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([exchange.UserA.Id, exchange.UserB.Id], token);
            var session = await _sessions.FindAsync(sessionId, token);
            if (session is null || !session.StartsAt(startAtTicks) || session.Status != SessionStatus.Scheduled
                || session.TelegramReminderSent(minutesBefore) || Now() >= session.StartAt)
            {
                return false;
            }

            session.MarkTelegramReminderSent(minutesBefore);
            await _sessions.SaveAsync(token);
            return true;
        }, ct);

        if (!send)
        {
            return;
        }

        foreach (var (self, partner) in new[] { (exchange.UserA, exchange.UserB), (exchange.UserB, exchange.UserA) })
        {
            if (await RecipientAsync(self.Id, NotificationCategory.LessonReminders, ct) is { } user)
            {
                Deliver(user.Id, TelegramTexts.Reminder(user.UiLanguage, partner.Name, minutesBefore, peek.StartAt, user.TimeZone, _frontend.ExchangeUrl(peek.ExchangeId)));
            }
        }
    }

    /// <summary>Запланированный урок отменён из-за остановки обмена (прерван, заморожен, удалён аккаунт).</summary>
    public async Task LessonCancelledByExchangeAsync(Guid sessionId)
    {
        var ct = CancellationToken.None;
        var session = (await _sessions.GetManyAsync([sessionId], ct)).GetValueOrDefault(sessionId);
        if (session is null || session.Status != SessionStatus.Cancelled || session.CancelledById is not null || session.StartAt <= Now())
        {
            return;
        }

        var exchange = await _exchanges.GetViewAsync(session.ExchangeId, ct);
        if (exchange is null)
        {
            return;
        }

        foreach (var self in new[] { exchange.UserA, exchange.UserB })
        {
            if (await RecipientAsync(self.Id, NotificationCategory.LessonChanges, ct) is { } user)
            {
                Deliver(user.Id, TelegramTexts.LessonCancelledByExchange(user.UiLanguage, session.StartAt, user.TimeZone, _frontend.ExchangeUrl(session.ExchangeId)));
            }
        }
    }

    /// <summary>Получатель, если ему можно писать: функция включена, аккаунт действует, Telegram подключён и группа не выключена.</summary>
    private async Task<Domain.Entities.User?> RecipientAsync(Guid userId, NotificationCategory category, CancellationToken ct)
    {
        if (!_options.IsConfigured || await _store.FindByUserAsync(userId, ct) is null || !await _store.IsEnabledAsync(userId, category, NotificationChannel.Telegram, ct))
        {
            return null;
        }

        var user = await _profiles.FindUserAsync(userId, ct);
        return user is { IsRegular: true, Status: UserStatus.Active } ? user : null;
    }

    private void Deliver(Guid userId, TelegramMessage message)
    {
        var (text, label, url) = (message.Text, message.Button.Text, message.Button.Url);
        _jobs.Enqueue<TelegramDeliveryJobs>(j => j.DeliverAsync(userId, text, label, url, 1));
    }
}

/// <summary>Сбой сети или 5xx Telegram: задача бросает это исключение, и Hangfire повторяет её позже.</summary>
public sealed class TelegramUnavailableException : Exception
{
    public TelegramUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>Отправка одного сообщения с учётом лимитов Telegram: 429 — повтор через <c>retry_after</c>, 403 — снять связь, 5xx и сеть — повторы Hangfire.</summary>
public sealed class TelegramDeliveryJobs
{
    /// <summary>Сколько раз ждать по 429, прежде чем отказаться.</summary>
    public const int MaxRateLimitAttempts = 8;

    private readonly ITelegramStore _store;
    private readonly ITelegramClient _client;
    private readonly TelegramService _service;
    private readonly IBackgroundJobs _jobs;
    private readonly TimeProvider _clock;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramDeliveryJobs> _logger;

    public TelegramDeliveryJobs(
        ITelegramStore store, ITelegramClient client, TelegramService service, IBackgroundJobs jobs, TimeProvider clock,
        IOptions<TelegramOptions> options, ILogger<TelegramDeliveryJobs> logger)
    {
        _store = store;
        _client = client;
        _service = service;
        _jobs = jobs;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task DeliverAsync(Guid userId, string text, string buttonText, string url, int attempt)
    {
        var ct = CancellationToken.None;
        if (!_options.IsConfigured || await _store.FindByUserAsync(userId, ct) is not { } link)
        {
            return;   // отключили, пока задача ждала
        }

        var result = await _client.SendMessageAsync(link.ChatId, text, new TelegramButton(buttonText, url), ct);
        switch (result.Status)
        {
            case TelegramSendStatus.Sent:
                return;
            case TelegramSendStatus.Blocked:
                await _service.UnlinkBlockedAsync(link.ChatId, ct);
                return;
            case TelegramSendStatus.RateLimited:
                if (attempt >= MaxRateLimitAttempts)
                {
                    _logger.LogWarning("Telegram: сообщение пользователю {UserId} не отправлено после {Attempts} ожиданий лимита.", userId, attempt);
                    return;
                }

                var wait = (result.RetryAfter ?? TimeSpan.FromSeconds(5)) + TimeSpan.FromSeconds(1);
                var next = attempt + 1;
                _jobs.Schedule<TelegramDeliveryJobs>(j => j.DeliverAsync(userId, text, buttonText, url, next), _clock.GetUtcNow() + wait);
                return;
            case TelegramSendStatus.Transient:
                throw new TelegramUnavailableException($"Telegram временно недоступен: {result.Description}");
            default:
                _logger.LogWarning("Telegram отклонил сообщение пользователю {UserId}: {Description}", userId, result.Description);
                return;
        }
    }
}
