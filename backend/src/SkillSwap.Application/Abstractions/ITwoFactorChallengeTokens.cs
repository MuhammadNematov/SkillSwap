namespace SkillSwap.Application.Abstractions;

/// <summary>Для чего выдан промежуточный токен 2FA.</summary>
public enum ChallengePurpose
{
    /// <summary>Пароль верный, 2FA включена: нужен код (POST /auth/login/2fa).</summary>
    Login,

    /// <summary>Пароль верный, но 2FA ещё не настроена, а для аккаунта она обязательна (служебные роли).</summary>
    Setup,
}

public sealed record Challenge(Guid UserId, ChallengePurpose Purpose);

/// <summary>
/// Короткоживущий (5 минут) подписанный токен «пароль проверен, нужен второй фактор».
/// Это не access-токен: на защищённых эндпоинтах он не принимается.
/// </summary>
public interface ITwoFactorChallengeTokens
{
    string Create(Guid userId, ChallengePurpose purpose);

    /// <summary>Возвращает null, если токен поддельный, просроченный или не от нас.</summary>
    Task<Challenge?> ValidateAsync(string token);
}
