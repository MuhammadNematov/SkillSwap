using SkillSwap.Application.Abstractions;

namespace SkillSwap.Application.Auth;

/// <summary>
/// Запрос на сброс пароля (FR-4). Ответ всегда один и тот же, зарегистрирован ли email или нет.
/// CaptchaToken — токен Cloudflare Turnstile (FR-5), проверяется раньше всего остального.
/// </summary>
public sealed record ForgotPasswordRequest(string Email, string? CaptchaToken = null) : ICaptchaRequest;

/// <summary>UserId и Token берутся из ссылки в письме (<c>/reset-password?userId=...&amp;token=...</c>).</summary>
public sealed record ResetPasswordRequest(Guid UserId, string Token, string NewPassword);
