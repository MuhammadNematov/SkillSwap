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
    private readonly IValidator<LoginTwoFactorRequest> _loginValidator;
    private readonly IValidator<TwoFactorVerifyRequest> _verifyValidator;
    private readonly IValidator<TwoFactorDisableRequest> _disableValidator;
    private readonly TimeProvider _clock;

    public TwoFactorService(
        UserManager<User> users,
        ITwoFactorChallengeTokens challenges,
        AuthTokenService tokens,
        IValidator<LoginTwoFactorRequest> loginValidator,
        IValidator<TwoFactorVerifyRequest> verifyValidator,
        IValidator<TwoFactorDisableRequest> disableValidator,
        TimeProvider clock)
    {
        _users = users;
        _challenges = challenges;
        _tokens = tokens;
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
            ? (await _users.RedeemTwoFactorRecoveryCodeAsync(user, NormalizeRecoveryCode(request.Code))).Succeeded
            : await VerifyTotpAsync(user, request.Code);

        if (!valid)
        {
            await _users.AccessFailedAsync(user);
            throw new UnauthorizedException("invalid_two_factor_code", "Неверный код.");
        }

        await _users.ResetAccessFailedCountAsync(user);
        user.LastActiveAt = _clock.GetUtcNow().UtcDateTime;
        await _users.UpdateAsync(user);

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

        await _users.ResetAuthenticatorKeyAsync(user);
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
        if (!await VerifyTotpAsync(user, request.Code))
        {
            await _users.AccessFailedAsync(user);
            throw new BadRequestException("invalid_two_factor_code", "Неверный код.");
        }

        await _users.ResetAccessFailedCountAsync(user);
        await _users.SetTwoFactorEnabledAsync(user, true);
        var codes = (await _users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount))?.ToList()
                    ?? throw new InvalidOperationException("Не удалось создать резервные коды.");

        if (!viaSetupToken)
        {
            return new TwoFactorVerifyResult(new TwoFactorVerifyResponse(codes, null), null, null);
        }

        // Служебный аккаунт прошёл первую настройку: пускаем сразу, чтобы не вводить код второй раз.
        user.LastActiveAt = _clock.GetUtcNow().UtcDateTime;
        await _users.UpdateAsync(user);
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

        if (!await VerifyTotpAsync(user, request.Code))
        {
            await _users.AccessFailedAsync(user);
            throw new BadRequestException("invalid_two_factor_code", "Неверный код.");
        }

        await _users.ResetAccessFailedCountAsync(user);
        await _users.SetTwoFactorEnabledAsync(user, false);
        await _users.ResetAuthenticatorKeyAsync(user);
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

    private Task<bool> VerifyTotpAsync(User user, string code) =>
        _users.VerifyTwoFactorTokenAsync(user, _users.Options.Tokens.AuthenticatorTokenProvider, NormalizeCode(code));

    /// <summary>Код из приложения вводят с пробелом или дефисом («123 456», «123-456»).</summary>
    private static string NormalizeCode(string code) => code.Replace(" ", string.Empty).Replace("-", string.Empty);

    /// <summary>
    /// Резервные коды Identity содержат дефис («xxxxxxxx-xxxxxxxx») и сравниваются как есть,
    /// поэтому убираем только пробелы по краям и внутри.
    /// </summary>
    private static string NormalizeRecoveryCode(string code) => code.Trim().Replace(" ", string.Empty);

    private static string BuildAuthenticatorUri(string email, string key) =>
        $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(email)}" +
        $"?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits=6";

    private static UnauthorizedException InvalidChallenge() =>
        new("invalid_two_factor_token", "Сеанс входа истёк. Войдите заново.");
}
