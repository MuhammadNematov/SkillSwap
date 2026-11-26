using System.Text.Json;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;
using SkillSwap.Application.Telegram;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Notifications;

/// <summary>Как создавать уведомления из сервисов: <see cref="Add"/> внутри транзакции, <see cref="Dispatch"/> после её фиксации.</summary>
public interface INotificationPublisher
{
    /// <summary>
    /// Создаёт уведомление в той же транзакции, что и изменение, которое его вызвало (сохраняется тем же <c>SaveChanges</c>):
    /// не бывает уведомления о том, чего не случилось, и случившегося без уведомления.
    /// </summary>
    void Add(Guid userId, NotificationType type, object payload);

    /// <summary>
    /// Отправляет созданное: ставит в очередь доставку в реальном времени и, для важных типов, письмо. Вызывать только
    /// после фиксации транзакции. Если до вызова произошло исключение, ничего не уходит.
    /// </summary>
    void Dispatch();
}

public sealed class NotificationPublisher : INotificationPublisher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly INotificationStore _store;
    private readonly IBackgroundJobs _jobs;
    private readonly TelegramOptions _telegram;
    private readonly List<Notification> _pending = [];

    public NotificationPublisher(INotificationStore store, IBackgroundJobs jobs, IOptions<TelegramOptions> telegram)
    {
        _store = store;
        _jobs = jobs;
        _telegram = telegram.Value;
    }

    public void Add(Guid userId, NotificationType type, object payload)
    {
        var notification = Notification.Create(userId, type, JsonSerializer.Serialize(payload, Json));
        _store.Add(notification);
        _pending.Add(notification);
    }

    public void Dispatch()
    {
        foreach (var notification in _pending)
        {
            var id = notification.Id;
            _jobs.Enqueue<NotificationJobs>(j => j.PushAsync(id));
            if (NotificationPolicy.SendsEmail(notification.Type))
            {
                _jobs.Enqueue<NotificationJobs>(j => j.EmailAsync(id));
            }

            if (_telegram.IsConfigured && NotificationCategories.ForTelegram(notification.Type) is not null)
            {
                _jobs.Enqueue<TelegramJobs>(j => j.NotificationAsync(id));
            }
        }

        _pending.Clear();
    }
}

/// <summary>
/// По email дублируется только важное (решение для этапа 4): новая заявка и принятая заявка; позже напоминания об уроках и решения по
/// обращениям (напоминания об уроках добавлены в шаге 7, решения по обращениям добавятся на этапе 6). Остальное только в интерфейсе.
/// </summary>
public static class NotificationPolicy
{
    public static bool SendsEmail(NotificationType type) =>
        type is NotificationType.RequestReceived or NotificationType.RequestAccepted or NotificationType.SessionReminder or NotificationType.CaseResolved or NotificationType.AppealDecided;
}
