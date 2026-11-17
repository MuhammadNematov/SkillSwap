namespace SkillSwap.Application.Auth;

/// <summary>Запрос на сброс пароля (FR-4). Ответ всегда один и тот же, зарегистрирован ли email или нет.</summary>
public sealed record ForgotPasswordRequest(string Email);

/// <summary>UserId и Token берутся из ссылки в письме (<c>/reset-password?userId=...&amp;token=...</c>).</summary>
public sealed record ResetPasswordRequest(Guid UserId, string Token, string NewPassword);
