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
    public static (string Subject, string Body)? Build(NotificationType type, string language, string name, JsonElement payload, FrontendOptions frontend)
    {
        var other = Text(payload, "userName") ?? "—";
        return type switch
        {
            NotificationType.RequestReceived => RequestReceived(language, name, other, frontend.RequestsUrl()),
            NotificationType.RequestAccepted => RequestAccepted(language, name, other, ExchangeLink(payload, frontend)),
            _ => null,
        };
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
