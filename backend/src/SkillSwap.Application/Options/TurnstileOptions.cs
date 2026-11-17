using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace SkillSwap.Application.Options;

/// <summary>
/// Cloudflare Turnstile (FR-5 ТЗ), секция "Turnstile".
/// <para>
/// SecretKey — секрет: только user-secrets или переменная окружения <c>Turnstile__SecretKey</c>, не в репозитории.
/// В appsettings.json его нет намеренно: иначе приложение могло бы молча работать с тестовым ключом.
/// </para>
/// <para>
/// Для разработки у Cloudflare есть публичные тестовые ключи (секрет всегда пускает: <c>1x0000000000000000000000000000000AA</c>;
/// всегда отказывает: <c>2x0000000000000000000000000000000AA</c>). Ключ сайта для виджета на фронтенде: <c>1x00000000000000000000AA</c>.
/// Вне Development тестовый секрет не принимается: приложение не запустится.
/// </para>
/// </summary>
public sealed class TurnstileOptions
{
    public const string SectionName = "Turnstile";

    /// <summary>Выключатель. Для тестов и локальной отладки; на боевом сервере капча должна быть включена.</summary>
    public bool Enabled { get; set; } = true;

    public string? SecretKey { get; set; }

    [Required, Url]
    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    /// <summary>Сколько ждать ответа Cloudflare. Дольше — считаем проверку недоступной.</summary>
    [Range(1, 30)]
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Передавать ли Cloudflare IP клиента (необязательное поле, улучшает оценку риска). По умолчанию нет:
    /// IP — персональные данные, и без настоящей необходимости мы их третьим лицам не отдаём.
    /// </summary>
    public bool SendClientIp { get; set; }

    /// <summary>Публичные тестовые секреты Cloudflare: 1x00…AA (всегда пускает), 2x00…AA (всегда отказывает), 3x00…AA (токен уже использован).</summary>
    public static bool IsDummySecret(string? key) => key is not null && DummySecretPattern.IsMatch(key);

    private static readonly Regex DummySecretPattern = new("^[123]x0+AA$", RegexOptions.Compiled);
}
