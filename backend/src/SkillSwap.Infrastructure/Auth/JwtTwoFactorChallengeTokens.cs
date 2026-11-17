using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;

namespace SkillSwap.Infrastructure.Auth;

/// <summary>
/// Промежуточный токен «пароль проверен, нужен второй фактор». Подписан тем же ключом, но с другой аудиторией
/// («...:2fa»), поэтому проверка access-токенов (JwtBearer) его отвергает, а он не годится как access-токен.
/// </summary>
public sealed class JwtTwoFactorChallengeTokens : ITwoFactorChallengeTokens
{
    private const string PurposeClaim = "purpose";

    private readonly JwtOptions _options;
    private readonly TimeProvider _clock;
    private readonly SymmetricSecurityKey _key;

    public JwtTwoFactorChallengeTokens(IOptions<JwtOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
    }

    private string Audience => _options.Audience + ":2fa";

    public string Create(Guid userId, ChallengePurpose purpose)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(_options.TwoFactorChallengeMinutes),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
                [PurposeClaim] = purpose.ToString(),
            },
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public async Task<Challenge?> ValidateAsync(string token)
    {
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = true,
            // Время берём из TimeProvider, чтобы срок жизни токена проверялся в тестах без ожидания.
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = _clock.GetUtcNow().UtcDateTime;
                var skew = TimeSpan.FromSeconds(30);
                return (notBefore is null || notBefore.Value <= now + skew) &&
                       (expires is null || expires.Value > now - skew);
            },
        });

        if (!result.IsValid)
        {
            return null;
        }

        var jwt = (JsonWebToken)result.SecurityToken;
        if (!Guid.TryParse(jwt.Subject, out var userId) ||
            !jwt.TryGetPayloadValue<string>(PurposeClaim, out var purposeValue) ||
            !Enum.TryParse<ChallengePurpose>(purposeValue, out var purpose))
        {
            return null;
        }

        return new Challenge(userId, purpose);
    }
}
