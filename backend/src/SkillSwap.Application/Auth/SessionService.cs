using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;

namespace SkillSwap.Application.Auth;

/// <summary>
/// Один действующий сеанс (устройство) пользователя. Id стабилен: он не меняется, когда сеанс обновляет токен.
/// </summary>
/// <param name="Id">Идентификатор сеанса (тот же, что в claim sid access-токена).</param>
/// <param name="DeviceInfo">User-Agent браузера (до 256 символов).</param>
/// <param name="IpAddress">IP последнего обновления токена.</param>
/// <param name="StartedAt">Когда сеанс начался (вход).</param>
/// <param name="LastActiveAt">Когда сеанс последний раз обновлял токен.</param>
/// <param name="ExpiresAt">Когда сеанс истечёт, если им не пользоваться.</param>
/// <param name="IsCurrent">Это сеанс, из которого сделан запрос.</param>
public sealed record SessionInfo(
    Guid Id,
    string? DeviceInfo,
    string? IpAddress,
    DateTime StartedAt,
    DateTime LastActiveAt,
    DateTime ExpiresAt,
    bool IsCurrent);

/// <summary>Активные сеансы пользователя и их завершение (раздел 10.1 ТЗ: «список активных сеансов, завершение одного или всех»).</summary>
public sealed class SessionService
{
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly TimeProvider _clock;

    public SessionService(IRefreshTokenRepository refreshTokens, TimeProvider clock)
    {
        _refreshTokens = refreshTokens;
        _clock = clock;
    }

    /// <param name="currentSessionId">Сеанс запроса (claim sid); null, если в токене его нет.</param>
    public async Task<IReadOnlyList<SessionInfo>> ListAsync(Guid userId, Guid? currentSessionId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var tokens = await _refreshTokens.ListActiveByUserAsync(userId, now, ct);

        return tokens
            .Select(t => new SessionInfo(
                t.SessionId, t.DeviceInfo, t.IpAddress, t.SessionStartedAt, t.CreatedAt, t.ExpiresAt, t.SessionId == currentSessionId))
            .ToList();
    }

    /// <summary>
    /// Завершает один сеанс. Чужой, уже завершённый и несуществующий сеанс неотличимы: везде 404,
    /// чтобы нельзя было перебором узнать идентификаторы чужих сеансов.
    /// </summary>
    public async Task RevokeAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var revoked = await _refreshTokens.RevokeSessionAsync(userId, sessionId, _clock.GetUtcNow().UtcDateTime, ct);
        if (revoked == 0)
        {
            throw new NotFoundException("session_not_found", "Сеанс не найден.");
        }
    }

    /// <summary>Завершает все сеансы, кроме текущего. Без sid в токене текущий неизвестен, а значит, «кроме него» сделать нельзя.</summary>
    public async Task<int> RevokeOthersAsync(Guid userId, Guid? currentSessionId, CancellationToken ct = default)
    {
        if (currentSessionId is not { } current)
        {
            throw new UnauthorizedException("invalid_session", "Токен устарел: войдите заново.");
        }

        return await _refreshTokens.RevokeAllExceptSessionAsync(userId, current, _clock.GetUtcNow().UtcDateTime, ct);
    }
}
