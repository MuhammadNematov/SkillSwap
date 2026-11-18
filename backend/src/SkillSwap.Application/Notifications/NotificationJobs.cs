using System.Text.Json;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Notifications;

/// <summary>Фоновая доставка уведомлений. Оба метода идемпотентны: повтор не создаёт дублей.</summary>
public sealed class NotificationJobs
{
    private readonly INotificationStore _store;
    private readonly INotificationPusher _pusher;
    private readonly IProfileStore _profiles;
    private readonly IMailSender _mail;
    private readonly FrontendOptions _frontend;
    private readonly TimeProvider _clock;

    public NotificationJobs(
        INotificationStore store, INotificationPusher pusher, IProfileStore profiles, IMailSender mail,
        IOptions<FrontendOptions> frontend, TimeProvider clock)
    {
        _store = store;
        _pusher = pusher;
        _profiles = profiles;
        _mail = mail;
        _frontend = frontend.Value;
        _clock = clock;
    }

    /// <summary>Показывает уведомление на открытых вкладках. Если пользователь не в сети, ничего не происходит: он увидит его в списке.</summary>
    public async Task PushAsync(Guid notificationId)
    {
        var notification = await _store.FindAsync(notificationId, CancellationToken.None);
        if (notification is null)
        {
            return;
        }

        await _pusher.PushAsync(notification.UserId, NotificationService.ToDto(notification), CancellationToken.None);
    }

    /// <summary>Письмо-дубль на языке пользователя; один раз на уведомление.</summary>
    public async Task EmailAsync(Guid notificationId)
    {
        var notification = await _store.FindAsync(notificationId, CancellationToken.None);
        if (notification is null || notification.EmailQueuedAt is not null || !NotificationPolicy.SendsEmail(notification.Type))
        {
            return;
        }

        var user = await _profiles.FindUserAsync(notification.UserId, CancellationToken.None);
        if (user is null || user.Status != UserStatus.Active || string.IsNullOrEmpty(user.Email) || !user.EmailConfirmed)
        {
            return;
        }

        using var payload = JsonDocument.Parse(notification.PayloadJson);
        var email = NotificationEmailTemplates.Build(notification.Type, user.UiLanguage, user.Name, payload.RootElement, _frontend);
        if (email is null)
        {
            return;
        }

        await _mail.SendAsync(user.Email, email.Value.Subject, email.Value.Body, CancellationToken.None);
        notification.MarkEmailQueued(_clock.GetUtcNow().UtcDateTime);
        await _store.SaveAsync(CancellationToken.None);
    }
}
