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

    public static (string Subject, string Body) ResetPassword(string language, string name, string link) => language switch
    {
        "ru" => (
            "SkillSwap: сброс пароля",
            $"Здравствуйте, {name}!\n\nЧтобы задать новый пароль, перейдите по ссылке (действует 1 час):\n{link}\n\nЕсли вы не запрашивали сброс, просто проигнорируйте это письмо: пароль не изменится."),
        _ => (
            "SkillSwap: password reset",
            $"Hello, {name}!\n\nTo set a new password, use this link (valid for 1 hour):\n{link}\n\nIf you did not request a reset, just ignore this email: your password will not change."),
    };

    public static (string Subject, string Body) PasswordChanged(string language, string name) => language switch
    {
        "ru" => (
            "SkillSwap: пароль изменён",
            $"Здравствуйте, {name}!\n\nПароль вашего аккаунта был изменён, все устройства выйдут из аккаунта. Если это сделали не вы, срочно сбросьте пароль заново и напишите в поддержку."),
        _ => (
            "SkillSwap: your password was changed",
            $"Hello, {name}!\n\nYour account password was changed and all devices will be signed out. If this was not you, reset your password again right away and contact support."),
    };
}