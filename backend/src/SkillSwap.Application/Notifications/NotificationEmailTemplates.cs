using System.Text.Json;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Notifications;

/// <summary>
/// Письма-дубли важных уведомлений. TODO: таджикский — после вычитки носителем языка (раздел 16.2 ТЗ); пока для tg английский текст.
/// Ссылки ведут на страницы фронтенда: <c>/requests</c> (заявки) и <c>/exchanges/{id}</c> (обмен).
/// </summary>
internal static class NotificationEmailTemplates
{
    public static (string Subject, string Body)? Build(NotificationType type, string language, string name, JsonElement payload, string baseUrl)
    {
        var other = Text(payload, "userName") ?? "—";
        return type switch
        {
            NotificationType.RequestReceived => RequestReceived(language, name, other, $"{baseUrl}/requests"),
            NotificationType.RequestAccepted => RequestAccepted(language, name, other, ExchangeLink(payload, baseUrl)),
            _ => null,
        };
    }

    private static string ExchangeLink(JsonElement payload, string baseUrl) =>
        Text(payload, "exchangeId") is { } id ? $"{baseUrl}/exchanges/{id}" : baseUrl;

    private static string? Text(JsonElement payload, string property) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out var value) ? value.ToString() : null;

    private static (string, string) RequestReceived(string language, string name, string other, string link) => language switch
    {
        "ru" => (
            "SkillSwap: новая заявка на обмен",
            $"Здравствуйте, {name}!\n\n{other} предлагает вам обмен навыками. Ответьте на заявку в течение 7 дней:\n{link}"),
        _ => (
            "SkillSwap: new exchange request",
            $"Hello, {name}!\n\n{other} wants to swap skills with you. Please reply within 7 days:\n{link}"),
    };

    private static (string, string) RequestAccepted(string language, string name, string other, string link) => language switch
    {
        "ru" => (
            "SkillSwap: заявка принята",
            $"Здравствуйте, {name}!\n\n{other} принял(а) вашу заявку, обмен начался. Откройте его и договоритесь о первом уроке:\n{link}"),
        _ => (
            "SkillSwap: your request was accepted",
            $"Hello, {name}!\n\n{other} accepted your request and the exchange has started. Open it to arrange your first lesson:\n{link}"),
    };
}
