using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Auth;

/// <summary>
/// Выпуск и ротация токенов (раздел 10.1 ТЗ): access JWT 15 минут, refresh 30 дней с ротацией.
/// Повторное использование уже заменённого refresh-токена отзывает все сеансы пользователя.
/// </summary>
public sealed class AuthTokenService
{
    private const int MaxDeviceInfoLength = 256;
    private const int MaxIpLength = 45;

    private readonly UserManager<User> _users;
    private readonly IAccessTokenGenerator _accessTokens;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly JwtOptions _jwt;
    private readonly TimeProvider _clock;

    public AuthTokenService(
        UserManager<User> users,
        IAccessTokenGenerator accessTokens,
        IRefreshTokenRepository refreshTokens,
        IOptions<JwtOptions> jwt,
        TimeProvider clock)
    {
        _users = users;
        _accessTokens = accessTokens;
        _refreshTokens = refreshTokens;
        _jwt = jwt.Value;
        _clock = clock;
    }

    /// <summary>Выпускает новую пару токенов (вход).</summary>
    public async Task<AuthResult> IssueAsync(User user, ClientInfo client, CancellationToken ct = default)
    {
        var result = CreateTokens(user, client, out var refreshEntity);
        _refreshTokens.Add(refreshEntity);
        await _refreshTokens.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Меняет действующий refresh-токен на новый.</summary>
    public async Task<AuthResult> RefreshAsync(string? rawToken, ClientInfo client, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw InvalidRefreshToken();
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var stored = await _refreshTokens.FindByHashAsync(Hash(rawToken), ct);
        if (stored is null)
        {
            throw InvalidRefreshToken();
        }

        if (stored.IsRevoked)
        {
            // Старый токен предъявили повторно: возможно, его украли. Отзываем все сеансы пользователя.
            await _refreshTokens.RevokeAllForUserAsync(stored.UserId, now, ct);
            throw new UnauthorizedException("refresh_token_reused", "Токен уже использован. Войдите заново.");
        }

        if (!stored.IsActive(now))
        {
            throw InvalidRefreshToken();
        }

        var user = await _users.FindByIdAsync(stored.UserId.ToString());
        if (user is null || user.Status == Domain.Enums.UserStatus.Deleted)
        {
            throw InvalidRefreshToken();
        }

        if (user.IsBlocked)
        {
            await _refreshTokens.RevokeAllForUserAsync(user.Id, now, ct);
            throw new ForbiddenException("account_blocked", "Аккаунт заблокирован.");
        }

        var result = CreateTokens(user, client, out var replacement);
        stored.Revoke(now, replacement.TokenHash);
        _refreshTokens.Add(replacement);

        // Два одновременных обновления одним токеном: второе упадёт на xmin → 409 (раздел 10.2 ТЗ).
        await _refreshTokens.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Завершает сеанс: отзывает переданный refresh-токен. Неизвестный токен игнорируется.</summary>
    public async Task RevokeAsync(string? rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return;
        }

        var stored = await _refreshTokens.FindByHashAsync(Hash(rawToken), ct);
        if (stored is null || stored.IsRevoked)
        {
            return;
        }

        stored.Revoke(_clock.GetUtcNow().UtcDateTime);
        await _refreshTokens.SaveChangesAsync(ct);
    }

    private AuthResult CreateTokens(User user, ClientInfo client, out RefreshToken refreshEntity)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var access = _accessTokens.Create(user);

        var rawRefresh = GenerateRawToken();
        var refreshExpires = now.AddDays(_jwt.RefreshTokenDays);
        refreshEntity = RefreshToken.Create(
            user.Id, Hash(rawRefresh), refreshExpires, Truncate(client.DeviceInfo, MaxDeviceInfoLength), Truncate(client.IpAddress, MaxIpLength));

        var summary = new UserSummary(user.Id, user.Email!, user.Name, user.Role, user.UiLanguage);
        return new AuthResult(new AuthResponse(access.Value, access.ExpiresAt, summary), rawRefresh, refreshExpires);
    }

    /// <summary>Случайный токен: 32 байта из криптостойкого генератора, base64url.</summary>
    private static string GenerateRawToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 в hex. Токен случайный и длинный, поэтому соль не нужна.</summary>
    internal static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private static UnauthorizedException InvalidRefreshToken() =>
        new("invalid_refresh_token", "Сеанс недействителен. Войдите заново.");
}
