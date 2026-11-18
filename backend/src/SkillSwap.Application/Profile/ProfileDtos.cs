namespace SkillSwap.Application.Profile;

/// <summary>Мой профиль (<c>GET /me</c>). Включает то, что видно только владельцу: email, дату рождения, 2FA.</summary>
public sealed record MyProfileDto(
    Guid Id,
    string Email,
    string Role,
    string AccountType,
    string Name,
    string? Bio,
    string? AvatarUrl,
    DateOnly? DateOfBirth,
    string UiLanguage,
    string TimeZone,
    bool IsSearchable,
    string[] Languages,
    bool SkillsComplete,
    decimal? RatingAvg,
    int RatingCount,
    bool IsNewcomer,
    bool TwoFactorEnabled,
    DateTime CreatedAt);

/// <summary>
/// Профиль для других пользователей (<c>GET /users/{id}</c>). Без email и даты рождения: только признак «16–17 лет».
/// Пока отзывов меньше 3 — «Новичок»: <c>isNewcomer: true</c>, средней оценки нет (<c>ratingAvg: null</c>).
/// </summary>
public sealed record PublicProfileDto(
    Guid Id,
    string Name,
    string? Bio,
    string? AvatarUrl,
    string TimeZone,
    string[] Languages,
    IReadOnlyList<PublicSkillDto> Teach,
    IReadOnlyList<PublicSkillDto> Learn,
    bool IsMinor,
    decimal? RatingAvg,
    int RatingCount,
    bool IsNewcomer,
    DateTime MemberSince);

/// <summary>PUT заменяет редактируемые поля целиком; <c>languages</c> и <c>isSearchable</c> обязательны (пропуск не должен молча менять их).</summary>
public sealed record UpdateProfileRequest(
    string Name,
    string? Bio,
    string UiLanguage,
    string TimeZone,
    string[]? Languages,
    bool? IsSearchable);
