using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Options;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Auth;

/// <summary>
/// Выпуск и ротация токенов (раздел 10.1 ТЗ): access JWT 15 минут, refresh 30 дней с ротацией.
/// Повторное использование уже заменённого refresh-токена отзывает все сеансы пользователя (кроме льготного окна: Auth:RefreshGraceSeconds).
/// </summary>
public sealed class AuthTokenService
{
    private const int MaxDeviceInfoLength = 256;
    private const int MaxIpLength = 45;

    /// <summary>Метка назначения ключа для HKDF: другой метке соответствует другой ключ, поэтому ключ преемников не совпадает ни с ключом подписи JWT, ни с ключами других назначений.</summary>
    internal const string SuccessorKeyLabel = "skillswap/refresh-successor-key/v1";

    private readonly UserManager<User> _users;
    private readonly IAccessTokenGenerator _accessTokens;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly JwtOptions _jwt;
    private readonly AuthOptions _auth;
    private readonly byte[] _successorKey;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;

    public AuthTokenService(
        UserManager<User> users,
        IAccessTokenGenerator accessTokens,
        IRefreshTokenRepository refreshTokens,
        IOptions<JwtOptions> jwt,
        IOptions<AuthOptions> auth,
        IAuditLog audit,
        TimeProvider clock)
    {
        _users = users;
        _accessTokens = accessTokens;
        _refreshTokens = refreshTokens;
        _jwt = jwt.Value;
        _auth = auth.Value;
        // Отдельный ключ для токенов-преемников: выводится из ключа подписи JWT через HKDF с меткой назначения, сам ключ подписи в HMAC не участвует
        _successorKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(_jwt.SigningKey), outputLength: 32, salt: null, info: Encoding.UTF8.GetBytes(SuccessorKeyLabel));
        _audit = audit;
        _clock = clock;
    }

    /// <summary>Выпускает новую пару токенов (вход) и начинает новый сеанс.</summary>
    public async Task<AuthResult> IssueAsync(User user, ClientInfo client, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var result = CreateTokens(user, client, sessionId: Guid.NewGuid(), sessionStartedAt: now, out var refreshEntity);
        _refreshTokens.Add(refreshEntity);
        await _refreshTokens.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>
    /// Меняет действующий refresh-токен на новый. Преемник выводится из предыдущего токена и ключа сервера (<see cref="DeriveSuccessor"/>), поэтому повтор
    /// в льготном окне (<see cref="AuthOptions.RefreshGraceSeconds"/>) возвращает тот же токен, а не новую ветку цепочки; параллельные запросы дают один и тот же преемник.
    /// </summary>
    public async Task<AuthResult> RefreshAsync(string? rawToken, ClientInfo client, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            throw InvalidRefreshToken();
        }

        var hash = Hash(rawToken);
        for (var attempt = 0; ; attempt++)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var stored = await _refreshTokens.FindByHashAsync(hash, ct) ?? throw InvalidRefreshToken();

            if (stored.IsRevoked)
            {
                // Токен, уже ЗАМЕНЁННЫЙ ротацией, предъявили повторно. В льготном окне и пока преемник не использован это повтор того же клиента (не кража):
                // отдаём прежнего преемника. Иначе возможна кража: отзываем все сеансы.
                if (stored.WasRotated)
                {
                    return await RepeatedRotatedTokenAsync(stored, client, now, ct);
                }

                // Токен отозван иначе (выход, завершение сеанса в списке сеансов, сброс пароля): это не кража.
                // Просто отказываем, не трогая остальные сеансы: иначе завершённое вручную устройство
                // при следующей попытке обновиться разлогинило бы все остальные.
                throw InvalidRefreshToken();
            }

            if (!stored.IsActive(now))
            {
                throw InvalidRefreshToken();
            }

            var user = await RequireUsableUserAsync(stored.UserId, now, ct);

            // Ротация продолжает тот же сеанс: SessionId и время начала переходят к новому токену.
            var successorRaw = DeriveSuccessor(stored.TokenHash);
            var result = CreateTokens(user, client, stored.SessionId, stored.SessionStartedAt, out var replacement, successorRaw);
            stored.Revoke(now, replacement.TokenHash);
            _refreshTokens.Add(replacement);

            // Два одновременных обновления одним токеном: проигравший (конфликт xmin или тот же хеш преемника) перечитывает токен и получает ответ по льготному окну
            if (await _refreshTokens.TrySaveRotationAsync(ct))
            {
                return result;
            }

            if (attempt >= 2)
            {
                throw new ConflictException("concurrent_refresh", "Обновление уже выполняется. Повторите запрос.");
            }
        }
    }

    /// <summary>Повторное предъявление заменённого токена: льготное окно (тот же преемник) либо отзыв всех сеансов.</summary>
    private async Task<AuthResult> RepeatedRotatedTokenAsync(RefreshToken stored, ClientInfo client, DateTime now, CancellationToken ct)
    {
        var withinGrace = _auth.RefreshGraceSeconds > 0 && stored.RevokedAt is { } revokedAt && now - revokedAt <= TimeSpan.FromSeconds(_auth.RefreshGraceSeconds);
        var successor = withinGrace ? await _refreshTokens.FindByHashAsync(stored.ReplacedByTokenHash!, ct) : null;

        if (successor is not null && !successor.IsRevoked)
        {
            var user = await RequireUsableUserAsync(stored.UserId, now, ct);
            var raw = DeriveSuccessor(stored.TokenHash);
            if (Hash(raw) == successor.TokenHash)
            {
                // Тот же токен, что уже выдан (он не использован); новый только access-токен. Цепочка не ветвится.
                await _audit.WriteAsync(new AuditEntry(stored.UserId, "RefreshGraceUsed", "Session", stored.SessionId, new
                {
                    graceSeconds = _auth.RefreshGraceSeconds,
                    secondsAfterRotation = Math.Round((now - stored.RevokedAt!.Value).TotalSeconds, 1),
                }), ct);
                var access = _accessTokens.Create(user, stored.SessionId);
                var summary = new UserSummary(user.Id, user.Email!, user.Name, user.Role, user.UiLanguage);
                return new AuthResult(new AuthResponse(access.Value, access.ExpiresAt, summary), raw, successor.ExpiresAt);
            }
        }

        if (successor is not null && successor.IsRevoked && !successor.WasRotated)
        {
            // Преемник закрыт иначе (выход, завершение сеанса): не кража
            throw InvalidRefreshToken();
        }

        // Окно прошло, либо преемник уже использован (WasRotated), либо его нет: прежняя защита от кражи
        await _refreshTokens.RevokeAllForUserAsync(stored.UserId, now, ct);
        throw new UnauthorizedException("refresh_token_reused", "Токен уже использован. Войдите заново.");
    }

    private async Task<User> RequireUsableUserAsync(Guid userId, DateTime now, CancellationToken ct)
    {
        var user = await _users.FindByIdAsync(userId.ToString());
        if (user is null || user.Status == Domain.Enums.UserStatus.Deleted)
        {
            throw InvalidRefreshToken();
        }

        if (user.IsBlocked)
        {
            await _refreshTokens.RevokeAllForUserAsync(user.Id, now, ct);
            throw new ForbiddenException("account_blocked", "Аккаунт заблокирован.");
        }

        return user;
    }

    /// <summary>
    /// Токен-преемник: HMAC-SHA256 от хеша предыдущего токена на отдельном ключе (HKDF от <c>Jwt:SigningKey</c> с меткой <see cref="SuccessorKeyLabel"/>), 32 байта, base64url.
    /// Без ключа его не восстановить ни по базе, ни по хешам, а с ключом повтор в льготном окне даёт ровно тот же токен, не храня сырой токен в базе.
    /// </summary>
    private string DeriveSuccessor(string previousTokenHash) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(_successorKey, Encoding.UTF8.GetBytes(previousTokenHash)));

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

    private AuthResult CreateTokens(
        User user, ClientInfo client, Guid sessionId, DateTime sessionStartedAt, out RefreshToken refreshEntity, string? rawRefreshOverride = null)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var access = _accessTokens.Create(user, sessionId);

        var rawRefresh = rawRefreshOverride ?? GenerateRawToken();
        var refreshExpires = now.AddDays(_jwt.RefreshTokenDays);
        refreshEntity = RefreshToken.Create(
            user.Id,
            Hash(rawRefresh),
            refreshExpires,
            Truncate(client.DeviceInfo, MaxDeviceInfoLength),
            Truncate(client.IpAddress, MaxIpLength),
            sessionId,
            sessionStartedAt);

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
