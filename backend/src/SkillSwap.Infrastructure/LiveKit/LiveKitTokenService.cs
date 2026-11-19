using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;

namespace SkillSwap.Infrastructure.LiveKit;

/// <summary>
/// Выпускает токены доступа в комнату LiveKit: JWT (HS256), подписанный API-секретом.
/// Формат — как у официальных SDK LiveKit: издатель (iss) = ключ API, sub и jti = идентификатор участника,
/// права — в claim <c>video</c>.
/// </summary>
public sealed class LiveKitTokenService : ILiveKitTokenService
{
    private readonly LiveKitOptions _options;
    private readonly TimeProvider _clock;

    public LiveKitTokenService(IOptions<LiveKitOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public LiveKitToken CreateJoinToken(string room, string identity, string? displayName)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException(
                "LiveKit не настроен: задайте LiveKit:ApiSecret (от 32 символов) через user-secrets или LiveKit__ApiSecret.");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var expires = now.AddSeconds(_options.TokenTtlSeconds);

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = identity,
            [JwtRegisteredClaimNames.Jti] = identity,
            // Права на одну комнату и только на вход в неё. Права администрирования (удалить комнату и т. п.) не выдаются.
            ["video"] = new Dictionary<string, object>
            {
                ["room"] = room,
                ["roomJoin"] = true,
                ["canPublish"] = true,
                ["canSubscribe"] = true,
                ["canPublishData"] = true,
            },
        };
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            claims["name"] = displayName;
        }

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.ApiKey,
            NotBefore = now,
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.ApiSecret!)),
                SecurityAlgorithms.HmacSha256),
        });

        return new LiveKitToken(token, expires);
    }

    public string CreateRoomDeleteToken(string room)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException("LiveKit не настроен: задайте LiveKit:ApiSecret.");
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.ApiKey,
            NotBefore = now,
            Expires = now.AddSeconds(60),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = "skillswap-server",
                // roomCreate в LiveKit — право создавать и удалять комнаты. Токен привязан к одной комнате.
                ["video"] = new Dictionary<string, object> { ["roomCreate"] = true, ["room"] = room },
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.ApiSecret!)),
                SecurityAlgorithms.HmacSha256),
        });
    }
}
