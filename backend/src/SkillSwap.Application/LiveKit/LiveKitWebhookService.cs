using System.Globalization;
using System.Text.Json;
using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.LiveKit;

public enum WebhookResult
{
    /// <summary>Новое событие записано.</summary>
    Accepted,

    /// <summary>Такое событие уже было (повторная доставка): игнорируется, но отвечаем успехом, чтобы LiveKit не слал снова.</summary>
    Duplicate,

    /// <summary>Подпись не прошла проверку. Причину намеренно не уточняем.</summary>
    Unauthorized,

    /// <summary>Подпись верна, но тело не похоже на событие LiveKit.</summary>
    Malformed,
}

/// <summary>
/// Приём событий от медиасервера (раздел 7.2 и 11.1 ТЗ). Порядок строгий: сначала подпись, и только потом
/// хоть что-то читается из тела: непроверенным данным не доверяем.
/// Событие записывается один раз, затем учитывается в посещаемости урока (<see cref="Sessions.VideoAttendanceService"/>).
/// </summary>
public sealed class LiveKitWebhookService
{
    // Пределы длины совпадают с колонками таблицы: длиннее — не событие LiveKit, а мусор.
    public const int EventIdMaxLength = 128;
    public const int TypeMaxLength = 64;
    public const int RoomNameMaxLength = 128;
    public const int IdentityMaxLength = 256;

    private readonly ILiveKitWebhookVerifier _verifier;
    private readonly ILiveKitWebhookEventStore _store;
    private readonly TimeProvider _clock;
    private readonly ILiveKitEventProcessor _attendance;

    public LiveKitWebhookService(
        ILiveKitWebhookVerifier verifier, ILiveKitWebhookEventStore store, TimeProvider clock, ILiveKitEventProcessor attendance)
    {
        _verifier = verifier;
        _store = store;
        _clock = clock;
        _attendance = attendance;
    }

    public async Task<WebhookResult> ReceiveAsync(string? authorization, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        if (!await _verifier.VerifyAsync(authorization, body))
        {
            return WebhookResult.Unauthorized;
        }

        var parsed = Parse(body.Span, _clock.GetUtcNow().UtcDateTime);
        if (parsed is null)
        {
            return WebhookResult.Malformed;
        }

        var isNew = await _store.TryAddAsync(parsed, ct);

        // Учёт в посещаемости идёт и для повторной доставки: если прошлая попытка упала, событие записано, но не обработано (ProcessedAt пусто).
        // Сбой обработки отвечает ошибкой, LiveKit повторит доставку. Уже обработанное пропускается.
        await _attendance.ProcessAsync(parsed.EventId, ct);
        return isNew ? WebhookResult.Accepted : WebhookResult.Duplicate;
    }

    /// <summary>
    /// Достаёт из события нужные поля. LiveKit присылает protobuf в JSON: число int64 может прийти строкой,
    /// неизвестные поля встречаются (появляются новые события), поэтому разбор терпим к лишнему и строг к нужному.
    /// </summary>
    internal static LiveKitWebhookEvent? Parse(ReadOnlySpan<byte> body, DateTime now)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var id = Text(root, "id");
            var type = Text(root, "event");
            if (id is null || type is null ||
                id.Length > EventIdMaxLength || type.Length > TypeMaxLength || !IsEventType(type))
            {
                return null;
            }

            var room = Nested(root, "room", "name");
            var identity = Nested(root, "participant", "identity");
            if ((room?.Length ?? 0) > RoomNameMaxLength || (identity?.Length ?? 0) > IdentityMaxLength)
            {
                return null;
            }

            return LiveKitWebhookEvent.Create(id, type, room, identity, OccurredAt(root) ?? now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Тип события: строчные латинские буквы, цифры и «_» (room_started, participant_joined, ...). Новые типы допустимы.</summary>
    private static bool IsEventType(string value) => value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string? Nested(JsonElement element, string objectName, string name) =>
        element.TryGetProperty(objectName, out var inner) && inner.ValueKind == JsonValueKind.Object ? Text(inner, name) : null;

    /// <summary>createdAt: секунды с начала эпохи, числом или строкой (так protobuf JSON записывает int64).</summary>
    private static DateTime? OccurredAt(JsonElement root)
    {
        if (!root.TryGetProperty("createdAt", out var value))
        {
            return null;
        }

        long seconds;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            seconds = number;
        }
        else if (value.ValueKind == JsonValueKind.String &&
                 long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            seconds = parsed;
        }
        else
        {
            return null;
        }

        // Разумные границы: от 2000 года до 2100-го. Иначе время не наше, берём время приёма.
        return seconds is >= 946_684_800 and <= 4_102_444_800
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;
    }
}
