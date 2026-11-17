namespace SkillSwap.Application.Abstractions;

/// <summary>
/// Запрос, защищённый капчей (FR-5 ТЗ). Токен приходит из виджета Cloudflare Turnstile на фронтенде
/// в поле <c>captchaToken</c> тела запроса.
/// </summary>
public interface ICaptchaRequest
{
    string? CaptchaToken { get; }
}

public enum CaptchaOutcome
{
    /// <summary>Капча пройдена.</summary>
    Passed,

    /// <summary>Токена нет (виджет не отработал или не передан).</summary>
    Missing,

    /// <summary>Токен не принят: неверный, просроченный или уже использованный (токен одноразовый).</summary>
    Invalid,

    /// <summary>
    /// Проверить не удалось: Cloudflare недоступен, ответил ошибкой или на нашей стороне неверно настроен секрет.
    /// Это не вина пользователя, поэтому ответ 503, а не 400. Запрос при этом не пропускается.
    /// </summary>
    Unavailable,
}

public sealed record CaptchaVerification(CaptchaOutcome Outcome, IReadOnlyList<string> ErrorCodes)
{
    public static CaptchaVerification Passed { get; } = new(CaptchaOutcome.Passed, []);
    public static CaptchaVerification Missing { get; } = new(CaptchaOutcome.Missing, []);

    public static CaptchaVerification Invalid(params string[] codes) => new(CaptchaOutcome.Invalid, codes);
    public static CaptchaVerification Unavailable(params string[] codes) => new(CaptchaOutcome.Unavailable, codes);
}

/// <summary>Проверка токена капчи у провайдера (Cloudflare Turnstile).</summary>
public interface ICaptchaVerifier
{
    /// <param name="token">Токен из виджета; null или пустой — капча не пройдена.</param>
    /// <param name="remoteIp">IP клиента, если его разрешено передавать провайдеру; иначе null.</param>
    Task<CaptchaVerification> VerifyAsync(string? token, string? remoteIp, CancellationToken ct = default);
}
