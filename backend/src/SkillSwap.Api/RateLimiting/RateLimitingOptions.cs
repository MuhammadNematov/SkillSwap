using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace SkillSwap.Api.RateLimiting;

/// <summary>
/// Одно правило: не более <see cref="PermitLimit"/> запросов за скользящее окно <see cref="WindowSeconds"/> секунд.
/// </summary>
public sealed class RateLimitRule
{
    public RateLimitRule()
    {
    }

    public RateLimitRule(int permitLimit, int windowSeconds)
    {
        PermitLimit = permitLimit;
        WindowSeconds = windowSeconds;
    }

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; }

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; }

    public TimeSpan Window => TimeSpan.FromSeconds(WindowSeconds);
}

/// <summary>
/// Ограничение частоты запросов (раздел 10.2 ТЗ: защита от перебора и спама). Секция "RateLimiting".
/// Все значения можно переопределить конфигурацией или переменными окружения, например
/// <c>RateLimiting__LoginPerIp__PermitLimit=20</c>.
/// Счётчики пока хранятся в памяти процесса (при нескольких экземплярах каждый считает свои); позже — Redis.
/// </summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Выключатель целиком (например, для нагрузочного теста). Не выключайте на боевом сервере.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Письма на один адрес (forgot-password и resend-confirmation вместе): 3 в час.</summary>
    [ValidateObjectMembers]
    public RateLimitRule MailPerEmail { get; set; } = new(3, 3600);

    /// <summary>Те же эндпоинты с одного IP: 10 в час.</summary>
    [ValidateObjectMembers]
    public RateLimitRule MailPerIp { get; set; } = new(10, 3600);

    /// <summary>login и login/2fa вместе, с одного IP: 10 в минуту.</summary>
    [ValidateObjectMembers]
    public RateLimitRule LoginPerIp { get; set; } = new(10, 60);

    /// <summary>Регистрация с одного IP: 5 в час.</summary>
    [ValidateObjectMembers]
    public RateLimitRule RegisterPerIp { get; set; } = new(5, 3600);

    /// <summary>Сброс пароля по ссылке (reset-password), с одного IP: 10 в час.</summary>
    [ValidateObjectMembers]
    public RateLimitRule ResetPasswordPerIp { get; set; } = new(10, 3600);

    /// <summary>Подтверждение email по ссылке (confirm-email), с одного IP: 20 в час.</summary>
    [ValidateObjectMembers]
    public RateLimitRule ConfirmEmailPerIp { get; set; } = new(20, 3600);

    /// <summary>Обновление токена (refresh), с одного IP: 30 в минуту.</summary>
    [ValidateObjectMembers]
    public RateLimitRule RefreshPerIp { get; set; } = new(30, 60);

    /// <summary>Остальные запросы авторизованного пользователя: 100 в минуту (считается по пользователю, не по IP).</summary>
    [ValidateObjectMembers]
    public RateLimitRule AuthenticatedPerUser { get; set; } = new(100, 60);
}
