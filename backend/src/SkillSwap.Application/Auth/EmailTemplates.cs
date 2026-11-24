namespace SkillSwap.Application.Auth;

/// <summary>
/// Тексты писем на трёх языках интерфейса: tg, ru, en (язык неизвестный или иной — английский).
/// TODO: таджикские тексты написаны без носителя языка и требуют вычитки (docs/DEFERRED.md, раздел 16.2 ТЗ).
/// </summary>
internal static class EmailTemplates
{
    /// <summary>Письмо подтверждения: шестизначный код, который вводится на сайте (ссылки нет).</summary>
    public static (string Subject, string Body) ConfirmEmail(string language, string name, string code) => language switch
    {
        "ru" => (
            $"SkillSwap: код подтверждения {code}",
            $"Здравствуйте, {name}!\n\nВаш код подтверждения:\n\n    {code}\n\nВведите его на сайте, чтобы завершить регистрацию. Код действует 15 минут.\n\nЕсли вы не регистрировались, просто проигнорируйте это письмо."),
        "tg" => (
            $"SkillSwap: рамзи тасдиқ {code}",
            $"Салом, {name}!\n\nРамзи тасдиқи шумо:\n\n    {code}\n\nБарои анҷом додани қайдшавӣ онро дар сайт ворид кунед. Рамз 15 дақиқа эътибор дорад.\n\nАгар шумо қайд нашуда бошед, ин мактубро нодида гиред."),
        _ => (
            $"SkillSwap: your confirmation code {code}",
            $"Hello, {name}!\n\nYour confirmation code:\n\n    {code}\n\nEnter it on the site to finish registration. The code is valid for 15 minutes.\n\nIf you did not sign up, just ignore this email."),
    };

    public static (string Subject, string Body) ResetPassword(string language, string name, string link) => language switch
    {
        "ru" => (
            "SkillSwap: сброс пароля",
            $"Здравствуйте, {name}!\n\nЧтобы задать новый пароль, перейдите по ссылке (действует 1 час):\n{link}\n\nЕсли вы не запрашивали сброс, просто проигнорируйте это письмо: пароль не изменится."),
        "tg" => (
            "SkillSwap: барқарор кардани парол",
            $"Салом, {name}!\n\nБарои гузоштани пароли нав ба ин истинод гузаред (1 соат эътибор дорад):\n{link}\n\nАгар шумо барқарорсозиро дархост накарда бошед, ин мактубро нодида гиред: пароли шумо тағйир намеёбад."),
        _ => (
            "SkillSwap: password reset",
            $"Hello, {name}!\n\nTo set a new password, use this link (valid for 1 hour):\n{link}\n\nIf you did not request a reset, just ignore this email: your password will not change."),
    };

    public static (string Subject, string Body) PasswordChanged(string language, string name) => language switch
    {
        "ru" => (
            "SkillSwap: пароль изменён",
            $"Здравствуйте, {name}!\n\nПароль вашего аккаунта был изменён, все устройства выйдут из аккаунта. Если это сделали не вы, срочно сбросьте пароль заново и напишите в поддержку."),
        "tg" => (
            "SkillSwap: пароли шумо тағйир дода шуд",
            $"Салом, {name}!\n\nПароли ҳисоби шумо тағйир дода шуд ва ҳамаи дастгоҳҳо аз ҳисоб мебароянд. Агар ин корро шумо накарда бошед, фавран паролро аз нав барқарор кунед ва ба дастгирӣ нависед."),
        _ => (
            "SkillSwap: your password was changed",
            $"Hello, {name}!\n\nYour account password was changed and all devices will be signed out. If this was not you, reset your password again right away and contact support."),
    };

    /// <summary>Письмо на старый адрес после удаления аккаунта (раздел 8.1 ТЗ).</summary>
    /// <summary>Письмо заблокированному: аккаунт заблокирован за нарушение правил платформы. Деталей обращения в письме нет.</summary>
    public static (string Subject, string Body) AccountBlocked(string language, string name) => language switch
    {
        "ru" => (
            "SkillSwap: аккаунт заблокирован",
            $"Здравствуйте, {name}!\n\nВаш аккаунт заблокирован за нарушение правил платформы. Войти в него больше нельзя."),
        "tg" => (
            "SkillSwap: аккаунт масдуд карда шуд",
            $"Салом, {name}!\n\nАккаунти шумо барои вайрон кардани қоидаҳои платформа масдуд карда шуд. Ворид шудан ба он дигар имконнопазир аст."),
        _ => (
            "SkillSwap: your account has been blocked",
            $"Hello, {name}!\n\nYour account has been blocked for violating the platform rules. You can no longer sign in."),
    };

    /// <summary>Письмо разблокированному: аккаунт снова действует, войти нужно заново. Деталей нет.</summary>
    public static (string Subject, string Body) AccountUnblocked(string language, string name) => language switch
    {
        "ru" => (
            "SkillSwap: аккаунт разблокирован",
            $"Здравствуйте, {name}!\n\nВаш аккаунт разблокирован. Войдите в него заново. Обмены, прерванные при блокировке, не возобновляются: вы можете начать новые."),
        "tg" => (
            "SkillSwap: аккаунт боз карда шуд",
            $"Салом, {name}!\n\nАккаунти шумо боз карда шуд. Бори дигар ворид шавед. Мубодилаҳое, ки ҳангоми масдуд кардан қатъ шуда буданд, аз сар гирифта намешаванд: шумо метавонед мубодилаҳои нав оғоз кунед."),
        _ => (
            "SkillSwap: your account has been unblocked",
            $"Hello, {name}!\n\nYour account has been unblocked. Please sign in again. Exchanges that were interrupted by the block are not restored: you can start new ones."),
    };

    public static (string Subject, string Body) AccountDeleted(string language, string name) => language switch
    {
        "ru" => (
            "SkillSwap: аккаунт удалён",
            $"Здравствуйте, {name}!\n\nВаш аккаунт SkillSwap удалён: личные данные стёрты, активные обмены завершены, все устройства вышли из аккаунта. Сообщения, которые вы писали партнёрам, остаются у них до плановой очистки чатов, автор в них отмечен как «Удалённый пользователь». Этот адрес можно использовать для новой регистрации.\n\nЕсли это сделали не вы, срочно напишите в поддержку."),
        "tg" => (
            "SkillSwap: ҳисоб нест карда шуд",
            $"Салом, {name}!\n\nҲисоби шумо дар SkillSwap нест карда шуд: маълумоти шахсӣ пок карда шуд, мубодилаҳои фаъол анҷом ёфтанд, ҳамаи дастгоҳҳо аз ҳисоб баромаданд. Паёмҳое, ки шумо ба ҳамкорон навиштаед, то тозакунии нақшавии чатҳо назди онҳо мемонанд, муаллиф дар онҳо «Корбари нест кардашуда» номида мешавад. Ин суроғаро барои сабти номи нав истифода бурдан мумкин аст.\n\nАгар ин корро шумо накарда бошед, фавран ба дастгирӣ нависед."),
        _ => (
            "SkillSwap: your account was deleted",
            $"Hello, {name}!\n\nYour SkillSwap account has been deleted: personal data was erased, active exchanges were ended and all devices were signed out. Messages you wrote to your partners stay with them until the scheduled chat cleanup, with you shown as \"Deleted user\". You can use this address to sign up again.\n\nIf this was not you, contact support right away."),
    };

    /// <summary>Письмо со ссылкой на выгрузку данных (раздел 8.1 ТЗ): ссылка действует 24 часа.</summary>
    public static (string Subject, string Body) DataExportReady(string language, string name, string link) => language switch
    {
        "ru" => (
            "SkillSwap: ваши данные готовы",
            $"Здравствуйте, {name}!\n\nВыгрузка ваших данных готова. Скачать файл (JSON) можно по ссылке, она действует 24 часа:\n{link}\n\nФайл содержит личные данные: не передавайте ссылку другим. Если вы не запрашивали выгрузку, смените пароль и напишите в поддержку."),
        "tg" => (
            "SkillSwap: маълумоти шумо тайёр аст",
            $"Салом, {name}!\n\nБорфарории маълумоти шумо тайёр аст. Файлро (JSON) тавассути ин истинод боргирӣ кунед, он 24 соат эътибор дорад:\n{link}\n\nФайл маълумоти шахсӣ дорад: истинодро ба дигарон надиҳед. Агар шумо борфарорӣ дархост накарда бошед, паролро иваз кунед ва ба дастгирӣ нависед."),
        _ => (
            "SkillSwap: your data is ready",
            $"Hello, {name}!\n\nYour data export is ready. Download the file (JSON) using this link, it is valid for 24 hours:\n{link}\n\nThe file contains personal data: do not share the link. If you did not request an export, change your password and contact support."),
    };
}
