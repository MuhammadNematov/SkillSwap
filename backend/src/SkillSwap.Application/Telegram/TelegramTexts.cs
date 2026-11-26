using System.Globalization;
using System.Text.Json;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Telegram;

public sealed record TelegramMessage(string Text, TelegramButton Button);

/// <summary>
/// Короткие тексты бота на tg, ru, en (иной язык — английский) с кнопкой-ссылкой на страницу сайта. В сообщениях нет паролей, кодов входа, почты,
/// текста переписки, оснований решений и других личных данных: только имя собеседника и время урока.
/// TODO: таджикские тексты требуют вычитки носителем языка (docs/DEFERRED.md).
/// </summary>
public static class TelegramTexts
{
    private static string Pick(string language, string ru, string tg, string en) => language switch { "ru" => ru, "tg" => tg, _ => en };

    public static string OpenButton(string language) => Pick(language, "Открыть", "Кушодан", "Open");

    public static string Linked(string language) => Pick(language,
        "Готово: Telegram подключён к вашему аккаунту SkillSwap. Здесь будут напоминания об уроках и важные события. Отключить: /stop",
        "Тайёр: Telegram ба ҳисоби SkillSwap-и шумо пайваст шуд. Дар ин ҷо ёдраскуниҳо дар бораи дарсҳо ва рӯйдодҳои муҳим мешаванд. Қатъ кардан: /stop",
        "Done: Telegram is connected to your SkillSwap account. Lesson reminders and important events will arrive here. To disconnect: /stop");

    public static string InvalidCode(string language) => Pick(language,
        "Ссылка недействительна или истекла. Откройте настройки на сайте и нажмите «Подключить» ещё раз.",
        "Истинод эътибор надорад ё мӯҳлаташ гузаштааст. Танзимоти сайтро кушоед ва боз «Пайваст кардан»-ро пахш кунед.",
        "This link is invalid or has expired. Open the settings on the website and press “Connect” again.");

    public static string StartHint(string language) => Pick(language,
        "Чтобы получать уведомления, откройте настройки на сайте SkillSwap и нажмите «Подключить» в блоке Telegram.",
        "Барои гирифтани огоҳиномаҳо танзимоти сайти SkillSwap-ро кушоед ва дар қисми Telegram «Пайваст кардан»-ро пахш кунед.",
        "To get notifications, open the settings on SkillSwap and press “Connect” in the Telegram section.");

    public static string Unlinked(string language) => Pick(language,
        "Telegram отключён: уведомления больше не приходят. Подключить снова можно в настройках на сайте.",
        "Telegram ҷудо карда шуд: огоҳиномаҳо дигар намеоянд. Боз пайваст кардан дар танзимоти сайт мумкин аст.",
        "Telegram is disconnected: no more notifications. You can connect it again in the website settings.");

    public static string NotLinked(string language) => Pick(language,
        "Этот чат не подключён ни к одному аккаунту.",
        "Ин чат ба ҳеҷ ҳисоб пайваст нест.",
        "This chat is not connected to any account.");

    public static TelegramMessage NewMessage(string language, string name, string url) => new(
        Pick(language, $"Новое сообщение от {name}", $"Паёми нав аз {name}", $"New message from {name}"),
        new TelegramButton(OpenButton(language), url));

    public static TelegramMessage Reminder(string language, string name, int minutesBefore, DateTime startAtUtc, string timeZone, string url)
    {
        var when = LocalTime(startAtUtc, timeZone);
        var text = minutesBefore >= 60
            ? Pick(language, $"Через 1 час урок с {name} ({when}).", $"Пас аз 1 соат дарс бо {name} ({when}).", $"Your lesson with {name} starts in 1 hour ({when}).")
            : Pick(language, $"Через 10 минут урок с {name} ({when}).", $"Пас аз 10 дақиқа дарс бо {name} ({when}).", $"Your lesson with {name} starts in 10 minutes ({when}).");
        return new TelegramMessage(text, new TelegramButton(OpenButton(language), url));
    }

    public static TelegramMessage LessonCancelledByExchange(string language, DateTime startAtUtc, string timeZone, string url) => new(
        Pick(language,
            $"Урок {LocalTime(startAtUtc, timeZone)} отменён: обмен остановлен.",
            $"Дарси {LocalTime(startAtUtc, timeZone)} бекор шуд: мубодила қатъ шуд.",
            $"The lesson on {LocalTime(startAtUtc, timeZone)} was cancelled: the exchange has been stopped."),
        new TelegramButton(OpenButton(language), url));

    /// <summary>Сообщение для уведомления интерфейса или null, если в Telegram по нему ничего не идёт.</summary>
    public static TelegramMessage? ForNotification(NotificationType type, string language, JsonElement payload, FrontendOptions frontend, string timeZone)
    {
        var name = Text(payload, "userName") ?? "—";
        string Exchange() => Guid.TryParse(Text(payload, "exchangeId"), out var id) ? frontend.ExchangeUrl(id) : frontend.BaseUrl.TrimEnd('/');
        string Case() => Guid.TryParse(Text(payload, "caseId"), out var id) ? frontend.CaseUrl(id) : frontend.BaseUrl.TrimEnd('/');
        TelegramMessage Make(string text, string url) => new(text, new TelegramButton(OpenButton(language), url));
        TelegramMessage CaseUpdate(string ru, string tg, string en) => Make(Pick(language, $"Обращение: {ru}", $"Муроҷиат: {tg}", $"Your case: {en}"), Case());

        switch (type)
        {
            case NotificationType.RequestReceived:
                return Make(Pick(language, $"Новая заявка на обмен от {name}", $"Дархости нави мубодила аз {name}", $"New exchange request from {name}"), frontend.RequestsUrl());
            case NotificationType.RequestAccepted:
                return Make(Pick(language, $"{name} принял(а) вашу заявку на обмен", $"{name} дархости мубодилаи шуморо қабул кард", $"{name} accepted your exchange request"), Exchange());
            case NotificationType.RequestDeclined:
                return Make(Pick(language, $"{name} отклонил(а) вашу заявку на обмен", $"{name} дархости мубодилаи шуморо рад кард", $"{name} declined your exchange request"), frontend.RequestsUrl());
            case NotificationType.SessionCancelled:
            {
                var when = LocalTime(Time(payload, "startAt"), timeZone);
                return Make(Pick(language, $"{name} отменил(а) урок {when}", $"{name} дарси {when}-ро бекор кард", $"{name} cancelled the lesson on {when}"), Exchange());
            }

            case NotificationType.SessionProposed:
            {
                // «Предложено время» — это перенос только когда есть прежнее время; новое предложение в Telegram не отправляется
                if (Time(payload, "previousStartAt") is null)
                {
                    return null;
                }

                var when = LocalTime(Time(payload, "startAt"), timeZone);
                return Make(Pick(language, $"{name} переносит урок на {when}. Подтвердите время.", $"{name} дарсро ба {when} мегузаронад. Вақтро тасдиқ кунед.", $"{name} moved the lesson to {when}. Please confirm the time."), Exchange());
            }

            case NotificationType.CaseTaken:
                return CaseUpdate("взято в работу", "ба кор гирифта шуд", "is being reviewed");
            case NotificationType.CaseReturnedToQueue:
                return CaseUpdate("возвращено в очередь", "ба навбат баргардонида шуд", "is back in the queue");
            case NotificationType.CaseExplanationRequested:
                return CaseUpdate("нужны ваши пояснения", "тавзеҳоти шумо лозим аст", "your explanation is needed");
            case NotificationType.CaseExplanationReminder:
                return CaseUpdate("срок ответа скоро истечёт", "мӯҳлати ҷавоб ба зудӣ ба охир мерасад", "the reply deadline is close");
            case NotificationType.CaseResolved:
                return CaseUpdate("принято решение", "қарор қабул шуд", "a decision was made");
            case NotificationType.CaseAppealed:
                return CaseUpdate("подана апелляция", "аппелятсия пешниҳод шуд", "an appeal was filed");
            case NotificationType.CaseAppealReviewAssigned:
                return CaseUpdate("апелляция на проверке", "аппелятсия дар санҷиш аст", "the appeal is being reviewed");
            case NotificationType.AppealDecided:
                return CaseUpdate("решение по апелляции принято", "қарор оид ба аппелятсия қабул шуд", "the appeal was decided");
            default:
                return null;
        }
    }

    private static DateTime? Time(JsonElement payload, string property) =>
        DateTime.TryParse(Text(payload, property), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc) ? utc : null;

    /// <summary>«20.11 18:00» в часовом поясе получателя; если пояс неизвестен системе — UTC с пометкой.</summary>
    public static string LocalTime(DateTime? utc, string timeZone)
    {
        if (utc is not { } value)
        {
            return "—";
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone)
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), zone).ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
            : value.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }

    private static string? Text(JsonElement payload, string property) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
}
