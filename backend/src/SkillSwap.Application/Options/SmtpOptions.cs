namespace SkillSwap.Application.Options;

/// <summary>Способ защиты соединения с SMTP-сервером.</summary>
public enum SmtpSecurity
{
    /// <summary>Без шифрования: только для локального Mailpit.</summary>
    None,

    /// <summary>Обычное соединение, затем STARTTLS (порт 587). Значение по умолчанию.</summary>
    StartTls,

    /// <summary>Сразу TLS (порт 465).</summary>
    SslOnConnect,
}

/// <summary>
/// Отправка писем по SMTP (секция "Smtp"). Пока <c>Host</c> не задан, письма в разработке пишутся в консоль, а вне разработки
/// приложение не запустится. Логин и пароль — только в переменных окружения или user-secrets
/// (<c>Smtp__User</c>, <c>Smtp__Password</c>), не в репозитории.
/// Локально письма ловит Mailpit из docker-compose: <c>Smtp:Host=localhost</c>, <c>Smtp:Port=1025</c>, <c>Smtp:Security=None</c>;
/// веб-интерфейс на http://localhost:8025.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;

    public string? User { get; set; }

    public string? Password { get; set; }

    /// <summary>Адрес отправителя («From»).</summary>
    public string FromAddress { get; set; } = "no-reply@skillswap.local";

    public string FromName { get; set; } = "SkillSwap";

    /// <summary>Сколько ждать сервер, секунд.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Проверять отзыв TLS-сертификата SMTP-сервера (по умолчанию да). Выключать только локально: macOS проверяет отзыв
    /// лишь по OCSP, а у части серверов (например, smtp.gmail.com) в сертификате только CRL, и MailKit рвёт соединение.
    /// Вне Development и Testing значение false не даёт приложению запуститься (<c>SmtpStartupCheck</c>).
    /// </summary>
    public bool CheckCertificateRevocation { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
