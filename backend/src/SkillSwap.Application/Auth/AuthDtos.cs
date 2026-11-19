using SkillSwap.Application.Abstractions;

namespace SkillSwap.Application.Auth;

/// <summary>
/// Регистрация (FR-1, FR-2, FR-3). UiLanguage необязателен: тогда берётся из Accept-Language.
/// CaptchaToken — токен Cloudflare Turnstile (FR-5), проверяется раньше всего остального.
/// </summary>
public sealed record RegisterRequest(
    string Email,
    string Password,
    string Name,
    DateOnly DateOfBirth,
    string? UiLanguage,
    bool AcceptTerms,
    bool AcceptChatModeration,
    string? CaptchaToken = null) : ICaptchaRequest;

public sealed record RegisterResponse(Guid UserId, string Email);

public sealed record ConfirmEmailRequest(Guid UserId, string Token);

public sealed record ResendConfirmationRequest(string Email);

/// <summary>Подтверждение email шестизначным кодом из письма (вместо ссылки). Пробелы и дефисы в коде допустимы.</summary>
public sealed record ConfirmEmailCodeRequest(string Email, string Code);
