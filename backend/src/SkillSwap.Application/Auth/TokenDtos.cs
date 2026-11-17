using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Auth;

public sealed record LoginRequest(string Email, string Password);

/// <summary>Краткие данные пользователя для интерфейса.</summary>
public sealed record UserSummary(Guid Id, string Email, string Name, UserRole Role, string UiLanguage);

/// <summary>Ответ на вход и обновление токена. Refresh-токен в тело не попадает — он только в cookie.</summary>
public sealed record AuthResponse(string AccessToken, DateTime AccessTokenExpiresAt, UserSummary User);

/// <summary>
/// Результат для контроллера: тело ответа + refresh-токен, который контроллер кладёт в HttpOnly-cookie.
/// </summary>
public sealed record AuthResult(AuthResponse Response, string RefreshToken, DateTime RefreshTokenExpiresAt);

/// <summary>Откуда пришёл запрос: для списка сеансов (раздел 10.1 ТЗ).</summary>
public sealed record ClientInfo(string? DeviceInfo, string? IpAddress);
