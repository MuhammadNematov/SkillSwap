using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;

namespace SkillSwap.Infrastructure.LiveKit;

/// <summary>
/// Проверка подписи webhook LiveKit (раздел 10.2 ТЗ). LiveKit кладёт в заголовок Authorization JWT, подписанный
/// API-секретом; в нём claim <c>sha256</c> — base64 хеша SHA-256 тела запроса. Проверяем и подпись, и хеш:
/// подпись без хеша позволила бы подменить тело перехваченного запроса.
/// </summary>
public sealed class LiveKitWebhookVerifier : ILiveKitWebhookVerifier
{
    private const string BodyHashClaim = "sha256";

    private readonly LiveKitOptions _options;
    private readonly TimeProvider _clock;

    public LiveKitWebhookVerifier(IOptions<LiveKitOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public async Task<bool> VerifyAsync(string? authorizationHeader, ReadOnlyMemory<byte> body)
    {
        // Нет секрета — проверить нечем, а значит, и пускать нельзя (не «пропускаем на время настройки»).
        if (!_options.IsConfigured || string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return false;
        }

        try
        {
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(authorizationHeader.Trim(), new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.ApiKey,
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.ApiSecret!)),
                // Только HS256: токен с «alg: none» или другим алгоритмом не принимается.
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                RequireSignedTokens = true,
                ValidateLifetime = true,
                LifetimeValidator = (notBefore, expires, _, _) =>
                {
                    var now = _clock.GetUtcNow().UtcDateTime;
                    var skew = TimeSpan.FromSeconds(30);
                    return (notBefore is null || notBefore.Value <= now + skew) &&
                           expires is { } exp && exp > now - skew;   // без срока действия не принимаем
                },
            });

            if (!result.IsValid ||
                result.SecurityToken is not JsonWebToken jwt ||
                !jwt.TryGetPayloadValue<string>(BodyHashClaim, out var claimedHash) ||
                string.IsNullOrEmpty(claimedHash))
            {
                return false;
            }

            var actualHash = Convert.ToBase64String(SHA256.HashData(body.Span));
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(claimedHash), Encoding.ASCII.GetBytes(actualHash));
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException or FormatException)
        {
            return false;   // мусор вместо токена — просто неверная подпись
        }
    }
}
