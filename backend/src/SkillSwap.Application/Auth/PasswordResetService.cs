using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Auth;

/// <summary>
/// Сброс пароля по ссылке из письма (FR-4). Токен одноразовый и живёт 1 час (его выпускает отдельный
/// поставщик токенов Identity, чтобы не менять срок ссылки подтверждения email).
/// </summary>
public sealed class PasswordResetService
{
    private readonly UserManager<User> _users;
    private readonly IMailSender _mail;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IValidator<ForgotPasswordRequest> _forgotValidator;
    private readonly IValidator<ResetPasswordRequest> _resetValidator;
    private readonly FrontendOptions _frontend;
    private readonly TimeProvider _clock;
    private readonly ILogger<PasswordResetService> _logger;

    public PasswordResetService(
        UserManager<User> users,
        IMailSender mail,
        IRefreshTokenRepository refreshTokens,
        IValidator<ForgotPasswordRequest> forgotValidator,
        IValidator<ResetPasswordRequest> resetValidator,
        IOptions<FrontendOptions> frontend,
        TimeProvider clock,
        ILogger<PasswordResetService> logger)
    {
        _users = users;
        _mail = mail;
        _refreshTokens = refreshTokens;
        _forgotValidator = forgotValidator;
        _resetValidator = resetValidator;
        _frontend = frontend.Value;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Отправляет ссылку для сброса, если такой подтверждённый и активный аккаунт есть.
    /// Всегда завершается одинаково (без ошибки) для известного и неизвестного email,
    /// чтобы по ответу нельзя было узнать, кто зарегистрирован.
    /// </summary>
    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken ct = default)
    {
        await _forgotValidator.ValidateAndThrowAsync(request, ct);

        var user = await _users.FindByEmailAsync(request.Email.Trim());

        // Неподтверждённым и заблокированным письмо не шлём: подтверждённый email — доказательство владения ящиком.
        if (user is null || !user.EmailConfirmed || user.Status != UserStatus.Active)
        {
            return;
        }

        try
        {
            var token = await _users.GeneratePasswordResetTokenAsync(user);
            var link = $"{_frontend.BaseUrl.TrimEnd('/')}/reset-password" +
                       $"?userId={user.Id}&token={UrlSafeToken.Encode(token)}";
            var (subject, body) = EmailTemplates.ResetPassword(user.UiLanguage, user.Name, link);
            await _mail.SendAsync(user.Email!, subject, body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ошибку клиенту не показываем: иначе по ней (500 или нет) можно отличить существующий email.
            _logger.LogWarning(ex, "Не удалось отправить письмо сброса пароля пользователю {UserId}", user.Id);
        }
    }

    /// <summary>
    /// Задаёт новый пароль. После успеха:
    /// отзываются все refresh-токены пользователя (все устройства выходят), снимается блокировка входа,
    /// а владельцу уходит письмо-уведомление. 2FA не затрагивается: вход по-прежнему потребует код.
    /// </summary>
    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken ct = default)
    {
        // Валидация идёт ДО проверки токена: слабый пароль не «сжигает» одноразовую ссылку.
        await _resetValidator.ValidateAndThrowAsync(request, ct);

        // Неизвестный пользователь, заблокированный, неверный или просроченный токен — один и тот же ответ.
        var user = await _users.FindByIdAsync(request.UserId.ToString());
        if (user is null || user.Status != UserStatus.Active)
        {
            throw InvalidResetToken();
        }

        var result = await _users.ResetPasswordAsync(user, UrlSafeToken.Decode(request.Token), request.NewPassword);
        if (!result.Succeeded)
        {
            throw MapResetError(result);
        }

        // Атакующий мог заблокировать аккаунт неверными паролями: владелец почты вправе войти снова.
        await _users.ResetAccessFailedCountAsync(user);
        if (await _users.IsLockedOutAsync(user))
        {
            await _users.SetLockoutEndDateAsync(user, null);   // пишем в строку пользователя только если есть что снимать
        }

        // Главное следствие сброса: все refresh-токены (все сеансы) отзываются.
        await _refreshTokens.RevokeAllForUserAsync(user.Id, _clock.GetUtcNow().UtcDateTime, ct);

        await TrySendPasswordChangedAsync(user, ct);
    }

    private async Task TrySendPasswordChangedAsync(User user, CancellationToken ct)
    {
        try
        {
            var (subject, body) = EmailTemplates.PasswordChanged(user.UiLanguage, user.Name);
            await _mail.SendAsync(user.Email!, subject, body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось отправить уведомление о смене пароля пользователю {UserId}", user.Id);
        }
    }

    private static AppException MapResetError(IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
        {
            return new ConflictException("concurrency_conflict", "Запись была изменена другим запросом. Повторите действие.");
        }

        if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
        {
            return InvalidResetToken();
        }

        // Остальное — нарушение политики пароля Identity (основные случаи уже отсеяны валидатором).
        var codes = string.Join(", ", result.Errors.Select(e => e.Code));
        return new BadRequestException("password_rejected", $"Пароль не подходит: {codes}.");
    }

    private static BadRequestException InvalidResetToken() =>
        new("invalid_reset_token", "Ссылка для сброса пароля недействительна или устарела.");
}
