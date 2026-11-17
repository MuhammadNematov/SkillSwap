namespace SkillSwap.Application.Auth;

/// <summary>
/// Ответ на POST /auth/login (FR-6). Возможны три исхода:
/// вошёл (AccessToken + User), нужен код 2FA (Requires2fa) или нужно сначала настроить 2FA (Requires2faSetup).
/// В двух последних случаях в TwoFactorToken лежит промежуточный токен.
/// </summary>
public sealed record LoginResponse(
    bool Requires2fa,
    bool Requires2faSetup,
    string? TwoFactorToken,
    string? AccessToken,
    DateTime? AccessTokenExpiresAt,
    UserSummary? User)
{
    public static LoginResponse Authenticated(AuthResponse auth) =>
        new(false, false, null, auth.AccessToken, auth.AccessTokenExpiresAt, auth.User);

    public static LoginResponse NeedsCode(string twoFactorToken) =>
        new(true, false, twoFactorToken, null, null, null);

    public static LoginResponse NeedsSetup(string twoFactorToken) =>
        new(false, true, twoFactorToken, null, null, null);
}

/// <summary>Результат для контроллера: тело ответа + refresh-токен (только если вход завершён).</summary>
public sealed record LoginResult(LoginResponse Response, string? RefreshToken, DateTime? RefreshTokenExpiresAt)
{
    public static LoginResult From(AuthResult auth) =>
        new(LoginResponse.Authenticated(auth.Response), auth.RefreshToken, auth.RefreshTokenExpiresAt);
}

/// <summary>Второй шаг входа. Если IsRecoveryCode, в Code передаётся один из резервных кодов.</summary>
public sealed record LoginTwoFactorRequest(string TwoFactorToken, string Code, bool IsRecoveryCode);

/// <summary>TwoFactorToken нужен только служебным аккаунтам, которые ещё не вошли (иначе берётся Bearer).</summary>
public sealed record TwoFactorSetupRequest(string? TwoFactorToken);

/// <summary>SharedKey — ключ для ручного ввода, AuthenticatorUri — содержимое QR-кода (otpauth://).</summary>
public sealed record TwoFactorSetupResponse(string SharedKey, string AuthenticatorUri);

public sealed record TwoFactorVerifyRequest(string Code, string? TwoFactorToken);

/// <summary>RecoveryCodes показываются один раз. Auth заполнен, если вход завершился этим же вызовом (служебные аккаунты).</summary>
public sealed record TwoFactorVerifyResponse(IReadOnlyList<string> RecoveryCodes, AuthResponse? Auth);

public sealed record TwoFactorVerifyResult(TwoFactorVerifyResponse Response, string? RefreshToken, DateTime? RefreshTokenExpiresAt);

public sealed record TwoFactorDisableRequest(string Password, string Code);
