using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;

namespace SkillSwap.Api.DevTools;

/// <summary>
/// Инструменты для проверки связки с LiveKit. Подключаются ТОЛЬКО в среде Development (см. Program.cs): в остальных
/// средах этих маршрутов нет вообще (404), а не «закрыты проверкой». Это прототип этапа 2; настоящая видеокомната,
/// выдача токена только участникам сессии и только в её окне — этап 5.
/// </summary>
public static partial class LiveKitDevTools
{
    public const string DefaultRoom = "dev-test-room";

    // Тестовые комнаты начинаются с «dev-»: так прототип не может выдать токен в будущую настоящую комнату «session-{id}».
    [GeneratedRegex("^dev-[a-z0-9-]{1,59}$")]
    private static partial Regex RoomPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$")]
    private static partial Regex IdentityPattern();

    private static readonly Lazy<string?> Page = new(LoadPage);

    public static IEndpointRouteBuilder MapLiveKitDevTools(this IEndpointRouteBuilder app)
    {
        // Не часть договора с фронтендом: в OpenAPI/Scalar не показываем.
        var group = app.MapGroup("/dev/livekit").ExcludeFromDescription();

        group.MapGet("", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Page.Value is { } html
                ? Results.Text(html, "text/html; charset=utf-8")
                : Results.NotFound();
        });

        group.MapGet("token", (
            HttpContext http,
            ILiveKitTokenService tokens,
            IOptions<LiveKitOptions> options,
            string? room,
            string? identity,
            string? name) =>
        {
            http.Response.Headers.CacheControl = "no-store";   // токен нельзя кэшировать

            var settings = options.Value;
            if (!settings.IsConfigured)
            {
                return Problem(StatusCodes.Status503ServiceUnavailable, "livekit_not_configured",
                    "LiveKit не настроен: задайте LiveKit:ApiSecret (от 32 символов), тот же, что у LiveKit-сервера.");
            }

            room = string.IsNullOrWhiteSpace(room) ? DefaultRoom : room.Trim();
            if (!RoomPattern().IsMatch(room))
            {
                return Problem(StatusCodes.Status400BadRequest, "invalid_room",
                    "Название тестовой комнаты: «dev-» и затем до 59 строчных латинских букв, цифр или дефисов.");
            }

            identity = string.IsNullOrWhiteSpace(identity) ? "dev-" + Guid.NewGuid().ToString("N")[..8] : identity.Trim();
            if (!IdentityPattern().IsMatch(identity))
            {
                return Problem(StatusCodes.Status400BadRequest, "invalid_identity",
                    "Идентификатор участника: до 64 латинских букв, цифр, «_», «.» или «-».");
            }

            name = string.IsNullOrWhiteSpace(name) ? identity : name.Trim();
            if (name.Length > 64)
            {
                return Problem(StatusCodes.Status400BadRequest, "invalid_name", "Имя не длиннее 64 символов.");
            }

            var token = tokens.CreateJoinToken(room, identity, name);
            return Results.Ok(new { url = settings.Url, token = token.Value, room, identity, expiresAt = token.ExpiresAt });
        });

        return app;
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, title: detail, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static string? LoadPage()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("livekit-test.html");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
