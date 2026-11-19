using FluentValidation;
using Microsoft.AspNetCore.Identity;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Auth;

/// <summary>
/// 2FA по TOTP (FR-6, раздел 10.1 ТЗ): настройка, подтверждение с резервными кодами, второй шаг входа, отключение.
/// Неверный код считается неудачной попыткой входа: после 5 ошибок аккаунт блокируется на 15 минут,
/// иначе шестизначный код можно перебрать.
/// </summary>
public sealed class TwoFactorService
{
    public const int RecoveryCodeCount = 10;
    private const string Issuer = "SkillSwap";

    private readonly UserManager<User> _users;
    private readonly ITwoFactorChallengeTokens _challenges;
    private readonly AuthTokenService _tokens;
    private readonly IRecoveryCodeStore _recoveryCodes;
    private readonly ITwoFactorStepStore _steps;
    private readonly IUserActivityStore _activity;
    private readonly IValidator<LoginTwoFactorRequest> _loginValidator;
    private readonly IValidator<TwoFactorVerifyRequest> _verifyValidator;
    private readonly IValidator<TwoFactorDisableRequest> _disableValidator;
    private readonly TimeProvider _clock;

    public TwoFactorService(
        UserManager<User> users,
        ITwoFactorChallengeTokens challenges,
        AuthTokenService tokens,
        IRecoveryCodeStore recoveryCodes,
        ITwoFactorStepStore steps,
        IUserActivityStore activity,
        IValidator<LoginTwoFactorRequest> loginValidator,
        IValidator<TwoFactorVerifyRequest> verifyValidator,
        IValidator<TwoFactorDisableRequest> disableValidator,
        TimeProvider clock)
    {
        _users = users;
        _challenges = challenges;
        _tokens = tokens;
        _recoveryCodes = recoveryCodes;
        _steps = steps;
        _activity = activity;
        _loginValidator = loginValidator;
        _verifyValidator = verifyValidator;
        _disableValidator = disableValidator;
        _clock = clock;
    }

    /// <summary>Второй шаг входа (POST /auth/login/2fa).</summary>
    public async Task<LoginResult> LoginAsync(LoginTwoFactorRequest request, ClientInfo client, CancellationToken ct = default)
    {
        await _loginValidator.ValidateAndThrowAsync(request, ct);

        var challenge = await _challenges.ValidateAsync(request.TwoFactorToken);
        if (challenge is not { Purpose: ChallengePurpose.Login })
        {
            throw InvalidChallenge();
        }

        var user = await FindActiveUserAsync(challenge.UserId) ?? throw InvalidChallenge();
        EnsureEmailConfirmedAndNotBlocked(user);
        await EnsureNotLockedOutAsync(user);

        var valid = request.IsRecoveryCode
            ? await _recoveryCodes.TryRedeemAsync(user.Id, RecoveryCodes.Hash(request.Code), ct)
            : await VerifyTotpAsync(user, request.Code, ct);

        if (!valid)
        {
            await _users.AccessFailedAsync(user);
            throw new UnauthorizedException("invalid_two_factor_code", "Неверный код.");
        }

        await _users.ResetAccessFailedCountAsync(user);
        await _activity.TouchAsync(user.Id, _clock.GetUtcNow().UtcDateTime, ct);

        return LoginResult.From(await _tokens.IssueAsync(user, client, ct));
    }

    /// <summary>
    /// Шаг 1 настройки: выдаёт ключ и адрес для QR-кода. 2FA ещё не включена до <see cref="VerifyAsync"/>.
    /// Каждый вызов создаёт новый ключ. Пользователя определяют по Bearer-токену (userId)
    /// либо по токену настройки (служебный аккаунт, который ещё не вошёл).
    /// </summary>
    public async Task<TwoFactorSetupResponse> SetupAsync(Guid? authenticatedUserId, TwoFactorSetupRequest? request)
    {
        var (user, _) = await ResolveUserAsync(authenticatedUserId, request?.TwoFactorToken);
        if (user.TwoFactorEnabled)
        {
            throw new ConflictException("two_factor_already_enabled", "2FA уже включена.");
        }

        EnsureSucceeded(await _users.ResetAuthenticatorKeyAsync(user));
        await _steps.ResetAsync(user.Id);
        var key = await _users.GetAuthenticatorKeyAsync(user)
                  ?? throw new InvalidOperationException("Не удалось создать ключ аутентификатора.");

        return new TwoFactorSetupResponse(key, BuildAuthenticatorUri(user.Email!, key));
    }

    /// <summary>
    /// Шаг 2 настройки: проверяет первый код, включает 2FA и выдаёт 10 резервных кодов (показываются один раз).
    /// Если вызвано по токену настройки, сразу завершает вход.
    /// </summary>
    public async Task<TwoFactorVerifyResult> VerifyAsync(
        Guid? authenticatedUserId, TwoFactorVerifyRequest request, ClientInfo client, CancellationToken ct = default)
    {
        await _verifyValidator.ValidateAndThrowAsync(request, ct);

        var (user, viaSetupToken) = await ResolveUserAsync(authenticatedUserId, request.TwoFactorToken);
        if (user.TwoFactorEnabled)
        {
            throw new ConflictException("two_factor_already_enabled", "2FA уже включена.");
        }

        if (await _users.GetAuthenticatorKeyAsync(user) is null)
        {
            throw new BadRequestException("two_factor_not_set_up", "Сначала запросите ключ: POST /auth/2fa/setup.");
        }

        await EnsureNotLockedOutAsync(user);
        if (!await VerifyTotpAsync(user, request.Code, ct))
        {
            await _users.AccessFailedAsync(user);
            throw new BadRequestException("invalid_two_factor_code", "Неверный код.");
        }

        await _users.ResetAccessFailedCountAsync(user);
        EnsureSucceeded(await _users.SetTwoFactorEnabledAsync(user, true));

        // В БД уходят только хеши; открытые коды вернутся пользователю один раз, в этом ответе.
        var codes = RecoveryCodes.Generate(RecoveryCodeCount);
        await _recoveryCodes.ReplaceAsync(user.Id, codes.Select(RecoveryCodes.Hash).ToList(), ct);

        if (!viaSetupToken)
        {
            return new TwoFactorVerifyResult(new TwoFactorVerifyResponse(codes, null), null, null);
        }

        // Служебный аккаунт прошёл первую настройку: пускаем сразу, чтобы не вводить код второй раз.
        await _activity.TouchAsync(user.Id, _clock.GetUtcNow().UtcDateTime, ct);
        var auth = await _tokens.IssueAsync(user, client, ct);
        return new TwoFactorVerifyResult(new TwoFactorVerifyResponse(codes, auth.Response), auth.RefreshToken, auth.RefreshTokenExpiresAt);
    }

    /// <summary>Отключение 2FA: нужны пароль и действующий код. Для служебных аккаунтов запрещено.</summary>
    public async Task DisableAsync(Guid userId, TwoFactorDisableRequest request, CancellationToken ct = default)
    {
        await _disableValidator.ValidateAndThrowAsync(request, ct);

        var user = await FindActiveUserAsync(userId) ?? throw new UnauthorizedException("invalid_credentials", "Сеанс недействителен.");
        if (user.AccountType == AccountType.Staff)
        {
            throw new ForbiddenException("two_factor_mandatory", "Для служебных аккаунтов 2FA обязательна.");
        }

        if (!user.TwoFactorEnabled)
        {
            throw new BadRequestException("two_factor_not_enabled", "2FA не включена.");
        }

        await EnsureNotLockedOutAsync(user);
        if (!await _users.CheckPasswordAsync(user, request.Password))
        {
            await _users.AccessFailedAsync(user);
            throw new BadRequestException("invalid_password", "Неверный пароль.");
        }

        if (!await VerifyTotpAsync(user, request.Code, ct))
        {
            await _users.AccessFailedAsync(user);
            throw new BadRequestException("invalid_two_factor_code", "Неверный код.");
        }

        await _users.ResetAccessFailedCountAsync(user);
        EnsureSucceeded(await _users.SetTwoFactorEnabledAsync(user, false));
        EnsureSucceeded(await _users.ResetAuthenticatorKeyAsync(user));
        await _recoveryCodes.DeleteAllAsync(user.Id, ct);
        await _steps.ResetAsync(user.Id, ct);
    }

    private async Task<(User User, bool ViaSetupToken)> ResolveUserAsync(Guid? authenticatedUserId, string? twoFactorToken)
    {
        if (authenticatedUserId is { } id)
        {
            var user = await FindActiveUserAsync(id) ?? throw new UnauthorizedException("invalid_credentials", "Сеанс недействителен.");
            return (user, false);
        }

        var challenge = string.IsNullOrWhiteSpace(twoFactorToken) ? null : await _challenges.ValidateAsync(twoFactorToken);
        if (challenge is not { Purpose: ChallengePurpose.Setup })
        {
            throw InvalidChallenge();
        }

        var fromToken = await FindActiveUserAsync(challenge.UserId) ?? throw InvalidChallenge();
        EnsureEmailConfirmedAndNotBlocked(fromToken);
        return (fromToken, true);
    }

    /// <summary>
    /// Результат критичной операции Identity нельзя игнорировать: при параллельном изменении той же строки
    /// Identity возвращает ConcurrencyFailure, а не бросает исключение. Тогда 2FA могла бы «включиться» только на словах.
    /// </summary>
    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new ConflictException("concurrency_conflict", "Запись была изменена другим запросом. Повторите действие.");
        }
    }

    private async Task<User?> FindActiveUserAsync(Guid id)
    {
        var user = await _users.FindByIdAsync(id.ToString());
        return user is null || user.Status == UserStatus.Deleted ? null : user;
    }

    private static void EnsureEmailConfirmedAndNotBlocked(User user)
    {
        if (!user.EmailConfirmed)
        {
            throw new ForbiddenException("email_not_confirmed", "Подтвердите email.");
        }

        if (user.IsBlocked)
        {
            throw new ForbiddenException("account_blocked", "Аккаунт заблокирован.");
        }
    }

    private async Task EnsureNotLockedOutAsync(User user)
    {
        if (await _users.IsLockedOutAsync(user))
        {
            throw new TooManyRequestsException("account_locked_out", "Слишком много неудачных попыток. Попробуйте через 15 минут.");
        }
    }

    /// <summary>
    /// Код подходит, если он верен для шага из окна ±1 И этот шаг новее последнего использованного.
    /// Один и тот же код (и любой более старый) во второй раз не пройдёт.
    /// </summary>
    private async Task<bool> VerifyTotpAsync(User user, string code, CancellationToken ct)
    {
        var key = await _users.GetAuthenticatorKeyAsync(user);
        if (key is null)
        {
            return false;
        }

        var step = Totp.FindMatchingStep(key, NormalizeCode(code), _clock.GetUtcNow());
        return step is { } s && await _steps.TryUseStepAsync(user.Id, s, ct);
    }

    /// <summary>
    /// Проверка второго фактора для опасных действий (удаление аккаунта): код приложения или резервный код. Использованный код сгорает.
    /// Счёт неудачных попыток ведёт вызывающий.
    /// </summary>
    public async Task<bool> VerifySecondFactorAsync(User user, string code, bool isRecoveryCode, CancellationToken ct = default) =>
        isRecoveryCode
            ? await _recoveryCodes.TryRedeemAsync(user.Id, RecoveryCodes.Hash(code), ct)
            : await VerifyTotpAsync(user, code, ct);

    /// <summary>Код из приложения вводят с пробелом или дефисом («123 456», «123-456»).</summary>
    private static string NormalizeCode(string code) => code.Replace(" ", string.Empty).Replace("-", string.Empty);


    private static string BuildAuthenticatorUri(string email, string key) =>
        $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(email)}" +
        $"?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits=6";

    private static UnauthorizedException InvalidChallenge() =>
        new("invalid_two_factor_token", "Сеанс входа истёк. Войдите заново.");
}
