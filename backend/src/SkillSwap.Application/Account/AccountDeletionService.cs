using FluentValidation;
using Microsoft.AspNetCore.Identity;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Auth;
using SkillSwap.Application.Blocks;
using SkillSwap.Application.ExchangeRequests;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Notifications;
using SkillSwap.Application.Sessions;
using SkillSwap.Application.Telegram;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Account;

/// <summary>
/// Удаление аккаунта (DELETE /me, раздел 8.1 ТЗ). После подтверждения паролем (и кодом 2FA) в одной транзакции под блокировкой самого
/// пользователя и его партнёров: активные обмены прерываются без виновного (партнёр видит нейтральную причину), уроки отменяются,
/// ожидающие заявки закрываются, личные данные стираются, адрес почты заменяется обезличенным (его можно зарегистрировать заново),
/// все сеансы отзываются. Сообщения остаются у партнёров до обычной очистки через 90 дней, автор подписан как «Удалённый пользователь».
/// Пока по пользователю открыто обращение, личные данные сохраняются до его закрытия (<see cref="CompleteErasureAsync"/>).
/// </summary>
public sealed class AccountDeletionService
{
    private readonly UserManager<User> _users;
    private readonly TwoFactorService _twoFactor;
    private readonly IUnitOfWork _uow;
    private readonly IExchangeStore _exchanges;
    private readonly IExchangeRequestStore _requests;
    private readonly IUserBlockStore _blocks;
    private readonly IAccountStore _account;
    private readonly IErasureHold _hold;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IRecoveryCodeStore _recoveryCodes;
    private readonly ITwoFactorStepStore _steps;
    private readonly LessonSessionService _lessons;
    private readonly ITelegramStore _telegram;
    private readonly INotificationPublisher _notify;
    private readonly IFileStorage _storage;
    private readonly IMailSender _mail;
    private readonly IValidator<DeleteAccountRequest> _validator;
    private readonly TimeProvider _clock;

    public AccountDeletionService(
        UserManager<User> users, TwoFactorService twoFactor, IUnitOfWork uow, IExchangeStore exchanges, IExchangeRequestStore requests,
        IUserBlockStore blocks, IAccountStore account, IErasureHold hold, IRefreshTokenRepository refreshTokens, IRecoveryCodeStore recoveryCodes,
        ITwoFactorStepStore steps, LessonSessionService lessons, INotificationPublisher notify, IFileStorage storage, IMailSender mail,
        IValidator<DeleteAccountRequest> validator, ITelegramStore telegram, TimeProvider clock)
    {
        _telegram = telegram;
        _users = users;
        _twoFactor = twoFactor;
        _uow = uow;
        _exchanges = exchanges;
        _requests = requests;
        _blocks = blocks;
        _account = account;
        _hold = hold;
        _refreshTokens = refreshTokens;
        _recoveryCodes = recoveryCodes;
        _steps = steps;
        _lessons = lessons;
        _notify = notify;
        _storage = storage;
        _mail = mail;
        _validator = validator;
        _clock = clock;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    public async Task DeleteAsync(Guid userId, DeleteAccountRequest request, CancellationToken ct)
    {
        await _validator.ValidateAndThrowAsync(request, ct);

        var user = await _users.FindByIdAsync(userId.ToString()) ?? throw new UnauthorizedException("invalid_credentials", "Сеанс недействителен.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("staff_cannot_delete_account", "Служебный аккаунт так удалить нельзя.");
        }

        if (user.Status == UserStatus.Deleted)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт уже удалён.");
        }

        await ConfirmIdentityAsync(user, request, ct);

        var oldEmail = user.Email!;
        var language = user.UiLanguage;
        var name = user.Name;
        var avatarKey = user.AvatarKey;
        var keepData = await _hold.HasOpenCaseAsync(userId, ct);

        var cancelled = new List<LessonSessionDto>();
        await _uow.ExecuteAsync(async token =>
        {
            // Блокируем себя и всех партнёров по открытым обменам (по возрастанию id): параллельные действия партнёров подождут.
            var open = await _exchanges.FindOpenForUserForUpdateAsync(userId, token);
            await _uow.LockUsersAsync(open.SelectMany(e => new[] { e.UserAId, e.UserBId }).Append(userId), token);
            open = await _exchanges.FindOpenForUserForUpdateAsync(userId, token);   // перечитываем уже под блокировкой

            var now = Now();
            foreach (var exchange in open)
            {
                exchange.AbortByDeletion(now);
                _notify.Add(exchange.PartnerOf(userId), NotificationType.ExchangeAborted, new { exchangeId = exchange.Id, reason = ExchangeService.PartnerUnavailable });
                cancelled.AddRange(await _lessons.CancelOpenAsync(exchange.Id, exchange.UserAId, exchange.UserBId, token));
            }

            foreach (var pending in await _requests.FindPendingForUserForUpdateAsync(userId, token))
            {
                pending.Expire(now);   // как при блокировке: без уведомлений
            }

            await _blocks.RemoveAllForUserAsync(userId, token);
            await _account.DeleteNotificationsAsync(userId, token);
            await _telegram.DeleteUserDataAsync(userId, token);   // связь с Telegram, коды и настройки уведомлений
            await _account.AnonymizeNotificationNamesAsync(userId, Domain.Entities.User.DeletedDisplayName, token);

            var current = await _users.FindByIdAsync(userId.ToString()) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
            current.MarkDeleted(now, keepData);
            if (!keepData)
            {
                await _account.DeleteProfileDataAsync(userId, token);
            }

            await ReplaceIdentityAsync(current, token);
            await _exchanges.SaveAsync(token);
            await _refreshTokens.RevokeAllForUserAsync(userId, now, token);
            await _refreshTokens.SaveChangesAsync(token);
            return 0;
        }, ct);

        // После фиксации: аватар из хранилища, письмо на старый адрес, уведомления партнёрам, карточки уроков.
        if (!keepData && avatarKey is not null)
        {
            await TryDeleteAvatarAsync(avatarKey);
        }

        var (subject, body) = EmailTemplates.AccountDeleted(language, name);
        await _mail.SendAsync(oldEmail, subject, body, CancellationToken.None);
        _notify.Dispatch();
        await _lessons.PublishUpdatesAsync(cancelled);
    }

    /// <summary>
    /// Обращение по пользователю закрыто: личные данные стираются (то, что <see cref="DeleteAsync"/> отложил). Идемпотентно. Вызывается
    /// при закрытии обращения (этап 6).
    /// </summary>
    public async Task CompleteErasureAsync(Guid userId, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(userId.ToString());
        if (user is not { Status: UserStatus.Deleted, ErasurePending: true })
        {
            return;
        }

        var avatarKey = user.AvatarKey;
        await _uow.ExecuteAsync(async token =>
        {
            await _uow.LockUsersAsync([userId], token);
            var current = await _users.FindByIdAsync(userId.ToString());
            if (current is not { ErasurePending: true })
            {
                return 0;
            }

            current.WipePersonalData();
            await _account.DeleteProfileDataAsync(userId, token);
            EnsureSucceeded(await _users.UpdateAsync(current));
            return 0;
        }, ct);

        if (avatarKey is not null)
        {
            await TryDeleteAvatarAsync(avatarKey);
        }
    }

    private async Task ConfirmIdentityAsync(User user, DeleteAccountRequest request, CancellationToken ct)
    {
        if (await _users.IsLockedOutAsync(user))
        {
            throw new TooManyRequestsException("account_locked_out", "Слишком много неудачных попыток. Попробуйте через 15 минут.");
        }

        if (!await _users.CheckPasswordAsync(user, request.Password!))
        {
            await _users.AccessFailedAsync(user);
            throw new BadRequestException("invalid_password", "Неверный пароль.");
        }

        if (user.TwoFactorEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                throw new BadRequestException("two_factor_code_required", "Введите код двухфакторной аутентификации.");
            }

            if (!await _twoFactor.VerifySecondFactorAsync(user, request.Code, request.IsRecoveryCode, ct))
            {
                await _users.AccessFailedAsync(user);
                throw new BadRequestException("invalid_two_factor_code", "Неверный код.");
            }
        }

        await _users.ResetAccessFailedCountAsync(user);
    }

    /// <summary>Почта и логин заменяются обезличенными (адрес можно зарегистрировать заново), пароль и 2FA убираются.</summary>
    private async Task ReplaceIdentityAsync(User user, CancellationToken ct)
    {
        var anonymous = $"deleted-{user.Id:N}@deleted.skillswap.invalid";
        EnsureSucceeded(await _users.SetUserNameAsync(user, anonymous));
        EnsureSucceeded(await _users.SetEmailAsync(user, anonymous));
        EnsureSucceeded(await _users.RemovePasswordAsync(user));
        EnsureSucceeded(await _users.SetTwoFactorEnabledAsync(user, false));
        EnsureSucceeded(await _users.SetPhoneNumberAsync(user, null));
        await _recoveryCodes.DeleteAllAsync(user.Id, ct);
        await _steps.ResetAsync(user.Id, ct);
        EnsureSucceeded(await _users.UpdateAsync(user));
    }

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new ConflictException("concurrency_conflict", "Запись была изменена другим запросом. Повторите действие.");
        }
    }

    private async Task TryDeleteAvatarAsync(string key)
    {
        try
        {
            await _storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (ServiceUnavailableException)
        {
            // Ссылка в профиле уже убрана; объект останется до сверки бакета (docs/DEFERRED.md).
        }
    }
}

public sealed class DeleteAccountRequestValidator : AbstractValidator<DeleteAccountRequest>
{
    public DeleteAccountRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().WithErrorCode("required");
        RuleFor(x => x.Code).MaximumLength(64).WithErrorCode("too_long");
    }
}
