using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkillSwap.Api.IntegrationTests.Infrastructure;

namespace SkillSwap.Api.IntegrationTests.LiveKit;

/// <summary>
/// Подписывает webhook так, как это делает LiveKit (формат снят с настоящего сервера LiveKit 1.13.11):
/// заголовок Authorization = голый JWT (без «Bearer»), HS256, iss = ключ API, nbf/iat, exp = nbf + 5 минут,
/// claim sha256 = base64 хеша тела.
/// </summary>
public static class LiveKitSigner
{
    public static string BodyHash(string body) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    public sealed record Options(
        string Secret = ApiFactory.LiveKitApiSecret,
        string Issuer = ApiFactory.LiveKitApiKey,
        TimeSpan NotBeforeOffset = default,
        TimeSpan Lifetime = default,
        string? HashOf = null,
        bool OmitHash = false,
        bool OmitExpiry = false,
        string Algorithm = SecurityAlgorithms.HmacSha256);

    public static string Sign(string body, DateTime now, Options? options = null)
    {
        options ??= new Options();
        var lifetime = options.Lifetime == default ? TimeSpan.FromMinutes(5) : options.Lifetime;
        var notBefore = now + options.NotBeforeOffset;

        var claims = new Dictionary<string, object>();
        if (!options.OmitHash)
        {
            claims["sha256"] = BodyHash(options.HashOf ?? body);
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            IssuedAt = notBefore,
            NotBefore = notBefore,
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Secret)), options.Algorithm),
        };
        if (!options.OmitExpiry)
        {
            descriptor.Expires = notBefore + lifetime;
        }

        // По умолчанию обработчик сам подставляет exp (+60 минут), если срок не задан. Для проверки «токен без срока»
        // это нужно отключить, иначе срок в токене всё равно окажется.
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = !options.OmitExpiry }.CreateToken(descriptor);
    }

    /// <summary>
    /// Токен с корректной подписью HMAC другим алгоритмом (HS384, HS512), собранный вручную: IdentityModel отказывается
    /// подписывать им слишком коротким ключом. Нужен, чтобы проверить запрет алгоритма отдельно от проверки подписи.
    /// </summary>
    public static string SignWithAlgorithm(string body, DateTime now, string algorithm)
    {
        static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var unix = new DateTimeOffset(now).ToUnixTimeSeconds();
        var header = B64(Encoding.UTF8.GetBytes($$"""{"alg":"{{algorithm}}","typ":"JWT"}"""));
        var payload = B64(Encoding.UTF8.GetBytes(
            $$"""{"iss":"{{ApiFactory.LiveKitApiKey}}","nbf":{{unix}},"exp":{{unix + 300}},"sha256":"{{BodyHash(body)}}"}"""));
        var signingInput = Encoding.ASCII.GetBytes($"{header}.{payload}");
        var key = Encoding.UTF8.GetBytes(ApiFactory.LiveKitApiSecret);
        var signature = algorithm switch
        {
            SecurityAlgorithms.HmacSha384 => HMACSHA384.HashData(key, signingInput),
            SecurityAlgorithms.HmacSha512 => HMACSHA512.HashData(key, signingInput),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
        return $"{header}.{payload}.{B64(signature)}";
    }

    /// <summary>Токен с «alg: none»: ни подписи, ни проверки. Классическая атака на проверяющих JWT.</summary>
    public static string Unsigned(string body, DateTime now)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var unix = new DateTimeOffset(now).ToUnixTimeSeconds();
        var header = B64("""{"alg":"none","typ":"JWT"}""");
        var payload = B64($$"""{"iss":"{{ApiFactory.LiveKitApiKey}}","nbf":{{unix}},"exp":{{unix + 300}},"sha256":"{{BodyHash(body)}}"}""");
        return $"{header}.{payload}.";
    }
}

/// <summary>Тела событий в том виде, в каком их присылает LiveKit (взяты с настоящего сервера; секрет TURN заменён).</summary>
public static class LiveKitBodies
{
    // Объект room как у настоящего LiveKit.
    private const string Room =
        """{"sid":"RM_De3m49ztsvQQ", "name":"@ROOM@", "emptyTimeout":300, "departureTimeout":20, "creationTime":"1794940517", "creationTimeMs":"1794940517934", "turnPassword":"REDACTED", "enabledCodecs":[{"mime":"audio/opus"}, {"mime":"video/VP8"}]}""";

    private static string Fill(string template, string id, string room, string? identity, string? name, long createdAt) => template
        .Replace("@ROOMOBJ@", Room)
        .Replace("@ROOM@", room)
        .Replace("@ID@", id)
        .Replace("@IDENTITY@", identity ?? string.Empty)
        .Replace("@NAME@", name ?? string.Empty)
        .Replace("@AT@", createdAt.ToString());

    public static string RoomStarted(string id, string room, long createdAt = 1794940517) =>
        Fill("""{"event":"room_started", "room":@ROOMOBJ@, "id":"@ID@", "createdAt":"@AT@"}""", id, room, null, null, createdAt);

    public static string ParticipantJoined(string id, string room, string identity, string name = "Probe", long createdAt = 1794940518) =>
        Fill("""{"event":"participant_joined", "room":@ROOMOBJ@, "participant":{"sid":"PA_KF6VGSpuLaMu", "identity":"@IDENTITY@", "state":"ACTIVE", "joinedAt":"1794940517", "name":"@NAME@", "version":2, "permission":{"canSubscribe":true, "canPublish":true, "canPublishData":true}}, "id":"@ID@", "createdAt":"@AT@"}""", id, room, identity, name, createdAt);

    public static string ParticipantLeft(string id, string room, string identity, long createdAt = 1794940530) =>
        Fill("""{"event":"participant_left", "room":@ROOMOBJ@, "participant":{"sid":"PA_KF6VGSpuLaMu", "identity":"@IDENTITY@", "state":"DISCONNECTED", "disconnectReason":"CLIENT_INITIATED"}, "id":"@ID@", "createdAt":"@AT@"}""", id, room, identity, null, createdAt);

    public static string RoomFinished(string id, string room, long createdAt = 1794940539) =>
        Fill("""{"event":"room_finished", "room":@ROOMOBJ@, "id":"@ID@", "createdAt":"@AT@", "roomEndReason":"ROOM_END_IDLE_TIMEOUT"}""", id, room, null, null, createdAt);

    public static string NewEventId() => "EV_" + Guid.NewGuid().ToString("N")[..12];
    public static string NewRoom() => "dev-" + Guid.NewGuid().ToString("N")[..10];
}