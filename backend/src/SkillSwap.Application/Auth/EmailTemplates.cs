namespace SkillSwap.Application.Auth;

/// <summary>
/// Тексты писем. TODO: таджикский — после вычитки носителем языка (раздел 16.2 ТЗ);
/// пока для tg отправляется английский текст.
/// </summary>
internal static class EmailTemplates
{
    public static (string Subject, string Body) ConfirmEmail(string language, string name, string link) => language switch
    {
        "ru" => (
            "SkillSwap: подтвердите email",
            $"Здравствуйте, {name}!\n\nЧтобы завершить регистрацию, подтвердите email по ссылке:\n{link}\n\nЕсли вы не регистрировались, просто проигнорируйте это письмо."),
        _ => (
            "SkillSwap: confirm your email",
            $"Hello, {name}!\n\nTo finish registration, confirm your email using this link:\n{link}\n\nIf you did not sign up, just ignore this email."),
    };
}
