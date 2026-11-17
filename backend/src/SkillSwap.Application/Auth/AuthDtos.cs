namespace SkillSwap.Application.Auth;

/// <summary>Регистрация (FR-1, FR-2, FR-3). UiLanguage необязателен: тогда берётся из Accept-Language.</summary>
public sealed record RegisterRequest(
    string Email,
    string Password,
    string Name,
    DateOnly DateOfBirth,
    string? UiLanguage,
    bool AcceptTerms,
    bool AcceptChatModeration);

public sealed record RegisterResponse(Guid UserId, string Email);

public sealed record ConfirmEmailRequest(Guid UserId, string Token);

public sealed record ResendConfirmationRequest(string Email);
