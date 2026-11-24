using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;

namespace SkillSwap.Application.Auth;

/// <summary>
/// Один действующий сеанс (устройство) пользователя. Id стабилен: он не меняется, когда сеанс обновляет токен.
/// </summary>
/// <param name="Id">Идентификатор сеанса (тот же, что в claim sid access-токена).</param>
/// <param name="DeviceInfo">User-Agent браузера (до 256 символов). Нужен для отладки: в интерфейсе его не показывают, для людей есть <paramref name="DeviceName"/>.</param>
/// <param name="IpAddress">IP последнего обновления токена.</param>
/// <param name="StartedAt">Когда сеанс начался (вход).</param>
/// <param name="LastActiveAt">Когда сеанс последний раз обновлял токен.</param>
/// <param name="ExpiresAt">Когда сеанс истечёт, если им не пользоваться.</param>
/// <param name="IsCurrent">Это сеанс, из которого сделан запрос.</param>
/// <param name="DeviceName">Понятное название устройства для списка («Safari · macOS», «Chrome · Android», «Safari · iPhone»); <c>null</c>, если по User-Agent не определить (интерфейс пишет «Неизвестное устройство»).</param>
/// <param name="DeviceType">Вид устройства для значка: компьютер, телефон, планшет или неизвестно.</param>
public sealed record SessionInfo(
    Guid Id,
    string? DeviceInfo,
    string? IpAddress,
    DateTime StartedAt,
    DateTime LastActiveAt,
    DateTime ExpiresAt,
    bool IsCurrent,
    string? DeviceName,
    DeviceType DeviceType);

/// <summary>Вид устройства сеанса (для значка в списке).</summary>
public enum DeviceType
{
    Unknown,
    Desktop,
    Phone,
    Tablet,
}

/// <param name="Name">«Браузер · система» или только система; <c>null</c>, если устройство не определить.</param>
public sealed record DeviceDescription(string? Name, DeviceType Type)
{
    public static DeviceDescription Unknown { get; } = new(null, DeviceType.Unknown);
}

/// <summary>Разбор User-Agent в понятное название устройства. Только браузер, система и вид устройства: ни места, ни провайдера, ни модели.</summary>
public interface IDeviceInfoParser
{
    DeviceDescription Describe(string? userAgent);
}

/// <summary>Активные сеансы пользователя и их завершение (раздел 10.1 ТЗ: «список активных сеансов, завершение одного или всех»).</summary>
public sealed class SessionService
{
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly TimeProvider _clock;
    private readonly IDeviceInfoParser _devices;

    public SessionService(IRefreshTokenRepository refreshTokens, TimeProvider clock, IDeviceInfoParser devices)
    {
        _refreshTokens = refreshTokens;
        _clock = clock;
        _devices = devices;
    }

    /// <param name="currentSessionId">Сеанс запроса (claim sid); null, если в токене его нет.</param>
    public async Task<IReadOnlyList<SessionInfo>> ListAsync(Guid userId, Guid? currentSessionId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var tokens = await _refreshTokens.ListActiveByUserAsync(userId, now, ct);

        return tokens
            .Select(t =>
            {
                var device = _devices.Describe(t.DeviceInfo);
                return new SessionInfo(
                    t.SessionId, t.DeviceInfo, t.IpAddress, t.SessionStartedAt, t.CreatedAt, t.ExpiresAt, t.SessionId == currentSessionId, device.Name, device.Type);
            })
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
