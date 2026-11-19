using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Exchanges;
using SkillSwap.Application.Options;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Sessions;

/// <summary>
/// Токен для входа в видеокомнату урока (разделы 6.3, 7.2 ТЗ). Выдаётся только двум участникам обмена, только для подтверждённого урока
/// и только в окне комнаты (за 10 минут до начала и до начала + 75 минут); в замороженном обмене не выдаётся. Комната одна на урок:
/// <c>session-{SessionId}</c>, имя участника в LiveKit — его Id (по нему из событий webhook собирается присутствие).
/// </summary>
public sealed class VideoRoomService
{
    private readonly ILessonSessionStore _sessions;
    private readonly IExchangeStore _exchanges;
    private readonly IProfileStore _profiles;
    private readonly ILiveKitTokenService _tokens;
    private readonly IOptions<LiveKitOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<VideoRoomService> _logger;

    public VideoRoomService(
        ILessonSessionStore sessions, IExchangeStore exchanges, IProfileStore profiles, ILiveKitTokenService tokens,
        IOptions<LiveKitOptions> options, TimeProvider clock, ILogger<VideoRoomService> logger)
    {
        _sessions = sessions;
        _exchanges = exchanges;
        _profiles = profiles;
        _tokens = tokens;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Имя комнаты урока (раздел 7.2 ТЗ). Идентификатор в нижнем регистре, без скобок: <c>session-3f2b…</c>.</summary>
    public static string RoomName(Guid sessionId) => $"session-{sessionId:D}";

    /// <summary>Идентификатор сессии из имени комнаты; null, если имя не наше.</summary>
    public static Guid? TryParseRoomName(string? room) =>
        room is { Length: > 8 } && room.StartsWith("session-", StringComparison.Ordinal) && Guid.TryParseExact(room.AsSpan(8), "D", out var id)
            ? id
            : null;

    public async Task<JoinTokenDto> CreateJoinTokenAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Видеокомната доступна только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }

        var session = (await _sessions.GetManyAsync([sessionId], ct)).GetValueOrDefault(sessionId);
        var exchange = session is null ? null : await _exchanges.GetViewAsync(session.ExchangeId, ct);
        if (session is null || exchange is null || (exchange.UserA.Id != userId && exchange.UserB.Id != userId))
        {
            throw new NotFoundException("session_not_found", "Урок не найден.");   // чужой урок выглядит как несуществующий
        }

        switch (exchange.Status)
        {
            case ExchangeStatus.Frozen:
                throw new ConflictException("exchange_frozen", "Обмен заморожен: вход в видеокомнату закрыт.");
            case not ExchangeStatus.Active:
                throw new ConflictException("exchange_not_active", "Обмен закрыт: вход в видеокомнату недоступен.");
        }

        if (session.Status is not (SessionStatus.Scheduled or SessionStatus.InProgress or SessionStatus.NoShow or SessionStatus.Missed))
        {
            throw new ConflictException(
                "session_not_joinable", "В эту комнату сейчас войти нельзя: урок не подтверждён или уже закрыт.",
                new Dictionary<string, object?> { ["sessionStatus"] = session.Status.ToString() });
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (now < session.JoinOpensAt)
        {
            throw new ConflictException(
                "join_window_not_open", "Вход в комнату откроется за 10 минут до начала урока.",
                new Dictionary<string, object?> { ["opensAt"] = session.JoinOpensAt });
        }

        if (now >= session.RoomClosesAt)
        {
            throw new ConflictException(
                "join_window_closed", "Вход в комнату закрыт: прошло 30 минут после окончания урока.",
                new Dictionary<string, object?> { ["closedAt"] = session.RoomClosesAt });
        }

        var settings = _options.Value;
        if (!settings.IsConfigured)
        {
            throw new ServiceUnavailableException("livekit_not_configured", "Видеосервер не настроен.");
        }

        var room = RoomName(session.Id);
        var token = _tokens.CreateJoinToken(room, userId.ToString("D"), exchange.UserA.Id == userId ? exchange.UserA.Name : exchange.UserB.Name);
        _logger.LogInformation("Выдан токен входа в комнату {Room} (пользователь {UserId}).", room, userId);   // сам токен не логируем

        return new JoinTokenDto(token.Value, settings.Url, room, token.ExpiresAt, session.StartAt, session.EndAt, session.RoomClosesAt);
    }
}
