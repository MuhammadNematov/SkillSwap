using System.Text.Json;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Options;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Chat;

/// <summary>
/// Уведомления о сообщениях без спама. В интерфейсе: одно уведомление <c>NewMessages</c> на обмен и получателя, пока у него есть
/// непрочитанные; новые сообщения обновляют его (счётчик, время), а не создают новое; когда получатель читает чат, оно становится
/// прочитанным. По email: если сообщение не прочитано час — одно письмо, и не чаще раза в сутки на обмен (см. <see cref="UnreadMessagesJobs"/>).
/// </summary>
public sealed class NewMessagesNotifier
{
    /// <summary>Через сколько после сообщения без прочтения уходит письмо.</summary>
    public static readonly TimeSpan EmailDelay = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly INotificationStore _notifications;
    private readonly IMessageStore _messages;
    private readonly IUnitOfWork _uow;
    private readonly IBackgroundJobs _jobs;
    private readonly TimeProvider _clock;

    public NewMessagesNotifier(
        INotificationStore notifications, IMessageStore messages, IUnitOfWork uow, IBackgroundJobs jobs, TimeProvider clock)
    {
        _notifications = notifications;
        _messages = messages;
        _uow = uow;
        _jobs = jobs;
        _clock = clock;
    }

    /// <summary>Сообщение сохранено: создать или обновить уведомление получателя. Вызывать после сохранения сообщения.</summary>
    public async Task OnMessageAsync(Message message, Guid recipientId, Guid senderId, string senderName, CancellationToken ct)
    {
        Guid notificationId = default;
        var created = false;

        await _uow.ExecuteAsync(async token =>
        {
            // Блокировка получателя: два сообщения подряд обновляют одно уведомление по очереди, а не создают два.
            await _uow.LockUsersAsync([recipientId], token);
            var now = _clock.GetUtcNow().UtcDateTime;
            var unread = (await _messages.CountUnreadAsync(recipientId, [message.ExchangeId], token)).GetValueOrDefault(message.ExchangeId);
            var payload = JsonSerializer.Serialize(
                new { exchangeId = message.ExchangeId, userId = senderId, userName = senderName, count = Math.Max(unread, 1) }, Json);

            var existing = await _notifications.FindUnreadNewMessagesAsync(recipientId, message.ExchangeId, token);
            if (existing is not null)
            {
                existing.Refresh(payload, DateTime.UtcNow);   // как и время создания записей: его проставляет приложение по системным часам
                notificationId = existing.Id;
            }
            else
            {
                var notification = Notification.Create(recipientId, NotificationType.NewMessages, payload, message.ExchangeId);
                _notifications.Add(notification);
                notificationId = notification.Id;
                created = true;
            }

            await _notifications.SaveAsync(token);
            return 0;
        }, ct);

        // После фиксации: показать (или обновить на экране) уведомление. Клиент заменяет его по id.
        var id = notificationId;
        _jobs.Enqueue<NotificationJobs>(j => j.PushAsync(id));

        if (created)
        {
            // Проверка «не прочитано ли через час»: ставится при первом непрочитанном; сама перепланируется, пока нужно.
            var exchangeId = message.ExchangeId;
            _jobs.Schedule<UnreadMessagesJobs>(j => j.CheckAsync(recipientId, exchangeId), new DateTimeOffset(_clock.GetUtcNow().UtcDateTime + EmailDelay, TimeSpan.Zero));
        }
    }

    /// <summary>Получатель прочитал чат: уведомление по обмену становится прочитанным.</summary>
    public async Task OnReadAsync(Guid readerId, Guid exchangeId, CancellationToken ct)
    {
        var marked = await _notifications.MarkNewMessagesReadAsync(readerId, exchangeId, _clock.GetUtcNow().UtcDateTime, ct);
        if (marked > 0)
        {
            var notification = await _notifications.FindLatestNewMessagesAsync(readerId, exchangeId, ct);
            if (notification is not null)
            {
                var id = notification.Id;
                _jobs.Enqueue<NotificationJobs>(j => j.PushAsync(id));   // другие вкладки увидят уведомление прочитанным
            }
        }
    }
}

/// <summary>
/// Письмо «у вас непрочитанные сообщения»: если сообщение не прочитано час, уходит одно письмо, не чаще раза в сутки на обмен и
/// получателя; новое письмо только про сообщения, пришедшие после предыдущего письма. Текст сообщений в письмо не попадает.
/// Метод идемпотентен: повторные и одновременные запуски не дают второго письма.
/// </summary>
public sealed class UnreadMessagesJobs
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromHours(24);

    private readonly IMessageStore _messages;
    private readonly INotificationStore _notifications;
    private readonly IExchangeStore _exchanges;
    private readonly IProfileStore _profiles;
    private readonly IMailSender _mail;
    private readonly IUnitOfWork _uow;
    private readonly IBackgroundJobs _jobs;
    private readonly FrontendOptions _frontend;
    private readonly TimeProvider _clock;

    public UnreadMessagesJobs(
        IMessageStore messages, INotificationStore notifications, IExchangeStore exchanges, IProfileStore profiles, IMailSender mail,
        IUnitOfWork uow, IBackgroundJobs jobs, IOptions<FrontendOptions> frontend, TimeProvider clock)
    {
        _messages = messages;
        _notifications = notifications;
        _exchanges = exchanges;
        _profiles = profiles;
        _mail = mail;
        _uow = uow;
        _jobs = jobs;
        _frontend = frontend.Value;
        _clock = clock;
    }

    public async Task CheckAsync(Guid recipientId, Guid exchangeId)
    {
        var ct = CancellationToken.None;
        var user = await _profiles.FindUserAsync(recipientId, ct);
        var exchange = await _exchanges.GetViewAsync(exchangeId, ct);
        if (user is null || exchange is null || user.Status != UserStatus.Active || (exchange.UserA.Id != recipientId && exchange.UserB.Id != recipientId))
        {
            return;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (ChatAccess.Evaluate(exchange.Status, exchange.ClosedAt, now).Mode == ChatMode.Archived)
        {
            return;
        }

        var lastEmail = await _notifications.LastNewMessagesEmailAtAsync(recipientId, exchangeId, ct);
        var oldest = await _messages.OldestUnreadAtAsync(recipientId, exchangeId, lastEmail, ct);
        if (oldest is null)
        {
            return;   // всё прочитано (или нового после прошлого письма нет): письма не нужно
        }

        var due = oldest.Value + NewMessagesNotifier.EmailDelay;
        if (lastEmail is { } sent && sent + MinInterval > due)
        {
            due = sent + MinInterval;   // суточный лимит: не раньше чем через сутки после прошлого письма
        }

        if (now < due)
        {
            _jobs.Schedule<UnreadMessagesJobs>(j => j.CheckAsync(recipientId, exchangeId), new DateTimeOffset(due, TimeSpan.Zero));
            return;
        }

        if (string.IsNullOrEmpty(user.Email) || !user.EmailConfirmed)
        {
            return;
        }

        // Решение и отметка «письмо отправлено» — под блокировкой получателя и с повторной проверкой: параллельные запуски дадут одно письмо.
        var send = await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([recipientId], token);
            var again = await _notifications.LastNewMessagesEmailAtAsync(recipientId, exchangeId, token);
            if (again is { } last && (again != lastEmail || now - last < MinInterval))
            {
                return false;
            }

            var row = await _notifications.FindLatestNewMessagesAsync(recipientId, exchangeId, token);
            if (row is null)
            {
                return false;
            }

            row.RecordEmailSent(now);
            await _notifications.SaveAsync(token);
            return true;
        }, ct);

        if (!send)
        {
            return;
        }

        var unread = (await _messages.CountUnreadAsync(recipientId, [exchangeId], ct)).GetValueOrDefault(exchangeId);
        var partner = exchange.UserA.Id == recipientId ? exchange.UserB : exchange.UserA;
        var (subject, body) = NotificationEmailTemplates.UnreadMessages(user.UiLanguage, user.Name, partner.Name, Math.Max(unread, 1), _frontend.ExchangeUrl(exchangeId));
        await _mail.SendAsync(user.Email, subject, body, ct);
    }
}
