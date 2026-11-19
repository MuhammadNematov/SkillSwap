using System.Text.Json;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Notifications;

/// <summary>
/// Письма-дубли важных уведомлений на tg, ru, en (иной язык — английский). Ссылки строятся из настроек фронтенда
/// (<c>Frontend:BaseUrl</c>, <c>Frontend:RequestsPath</c>, <c>Frontend:ExchangePath</c>).
/// TODO: таджикские тексты требуют вычитки носителем языка (docs/DEFERRED.md).
/// </summary>
internal static class NotificationEmailTemplates
{
    public static (string Subject, string Body)? Build(NotificationType type, string language, string name, JsonElement payload, FrontendOptions frontend, string timeZone)
    {
        var other = Text(payload, "userName") ?? "—";
        return type switch
        {
            NotificationType.RequestReceived => RequestReceived(language, name, other, frontend.RequestsUrl()),
            NotificationType.RequestAccepted => RequestAccepted(language, name, other, ExchangeLink(payload, frontend)),
            NotificationType.SessionReminder => Reminder(language, name, other, payload, ExchangeLink(payload, frontend), timeZone),
            _ => null,
        };
    }

    /// <summary>Письмо о непрочитанных сообщениях (не чаще раза в сутки на обмен). Текст сообщений в письмо не попадает.</summary>
    public static (string Subject, string Body) UnreadMessages(string language, string name, string other, int count, string link) => language switch
    {
        "ru" => (
            "SkillSwap: у вас непрочитанные сообщения",
            $"Здравствуйте, {name}!\n\n{other} написал(а) вам в чате обмена. Непрочитанных сообщений: {count}. Откройте чат:\n{link}"),
        "tg" => (
            "SkillSwap: шумо паёмҳои нохондашуда доред",
            $"Салом, {name}!\n\n{other} дар чати мубодила ба шумо навишт. Шумораи паёмҳои нохондашуда: {count}. Чатро кушоед:\n{link}"),
        _ => (
            "SkillSwap: you have unread messages",
            $"Hello, {name}!\n\n{other} wrote to you in your exchange chat. Unread messages: {count}. Open the chat:\n{link}"),
    };

    /// <summary>
    /// Напоминание об уроке за 24 часа или за 15 минут. Время показывается в часовом поясе получателя (раздел 6 ТЗ): у каждого из двоих оно своё,
    /// в письме указан и сам пояс, чтобы не было путаницы.
    /// </summary>
    private static (string, string) Reminder(string language, string name, string other, JsonElement payload, string link, string timeZone)
    {
        var minutes = int.TryParse(Text(payload, "minutesBefore"), out var m) ? m : 15;
        var when = LocalTime(Text(payload, "startAt"), timeZone);
        var tg = minutes >= 1440 ? "пас аз 24 соат" : "пас аз 15 дақиқа";
        var ru = minutes >= 1440 ? "через 24 часа" : "через 15 минут";
        var en = minutes >= 1440 ? "in 24 hours" : "in 15 minutes";
        return language switch
        {
            "ru" => (
                $"SkillSwap: урок {ru}",
                $"Здравствуйте, {name}!\n\nНапоминаем: урок с {other} начнётся {ru}, {when}. Откройте обмен:\n{link}"),
            "tg" => (
                $"SkillSwap: дарс {tg}",
                $"Салом, {name}!\n\nЯдрас мекунем: дарс бо {other} {tg} оғоз мешавад, {when}. Мубодиларо кушоед:\n{link}"),
            _ => (
                $"SkillSwap: lesson {en}",
                $"Hello, {name}!\n\nReminder: your lesson with {other} starts {en}, {when}. Open the exchange:\n{link}"),
        };
    }

    /// <summary>«20.11.2026 18:00 (Asia/Dushanbe)»; если пояс неизвестен системе, время остаётся в UTC.</summary>
    private static string LocalTime(string? startAtUtc, string timeZone)
    {
        if (!DateTime.TryParse(startAtUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var utc))
        {
            return "—";
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone))
        {
            return $"{TimeZoneInfo.ConvertTimeFromUtc(utc, zone):dd.MM.yyyy HH:mm} ({timeZone})";
        }

        return $"{utc:dd.MM.yyyy HH:mm} (UTC)";
    }

    private static string ExchangeLink(JsonElement payload, FrontendOptions frontend) =>
        Guid.TryParse(Text(payload, "exchangeId"), out var id) ? frontend.ExchangeUrl(id) : frontend.BaseUrl.TrimEnd('/');

    private static string? Text(JsonElement payload, string property) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value) ? value.ToString() : null;

    private static (string, string) RequestReceived(string language, string name, string other, string link) => language switch
    {
        "ru" => (
            "SkillSwap: новая заявка на обмен",
            $"Здравствуйте, {name}!\n\n{other} предлагает вам обмен навыками. Ответьте на заявку в течение 7 дней:\n{link}"),
        "tg" => (
            "SkillSwap: дархости нави мубодила",
            $"Салом, {name}!\n\n{other} ба шумо мубодилаи малакаҳоро пешниҳод мекунад. Дар давоми 7 рӯз ба дархост ҷавоб диҳед:\n{link}"),
        _ => (
            "SkillSwap: new exchange request",
            $"Hello, {name}!\n\n{other} wants to swap skills with you. Please reply within 7 days:\n{link}"),
    };

    private static (string, string) RequestAccepted(string language, string name, string other, string link) => language switch
    {
        "ru" => (
            "SkillSwap: заявка принята",
            $"Здравствуйте, {name}!\n\n{other} принял(а) вашу заявку, обмен начался. Откройте его и договоритесь о первом уроке:\n{link}"),
        "tg" => (
            "SkillSwap: дархости шумо қабул шуд",
            $"Салом, {name}!\n\n{other} дархости шуморо қабул кард ва мубодила оғоз ёфт. Онро кушоед ва дар бораи дарси аввал мувофиқа кунед:\n{link}"),
        _ => (
            "SkillSwap: your request was accepted",
            $"Hello, {name}!\n\n{other} accepted your request and the exchange has started. Open it to arrange your first lesson:\n{link}"),
    };
}
