using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Options;
using SkillSwap.Infrastructure.LiveKit;

namespace SkillSwap.Api.IntegrationTests.LiveKit;

/// <summary>Проверка подписи webhook LiveKit (раздел 10.2 ТЗ): подпись, срок жизни, хеш тела, алгоритм.</summary>
public class LiveKitWebhookVerifierTests
{
    private const string Secret = ApiFactory.LiveKitApiSecret;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly string _body = LiveKitBodies.ParticipantJoined("EV_abc123", "dev-room", "alice-1", "Алиса");

    private LiveKitWebhookVerifier Verifier(string? secret = Secret, string apiKey = ApiFactory.LiveKitApiKey) =>
        new(Options.Create(new LiveKitOptions { ApiKey = apiKey, ApiSecret = secret }), _clock);

    private string Sign(string? body = null, LiveKitSigner.Options? options = null) =>
        LiveKitSigner.Sign(body ?? _body, _clock.GetUtcNow().UtcDateTime, options);

    private static ReadOnlyMemory<byte> Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private Task<bool> Verify(string? header, string? body = null, LiveKitWebhookVerifier? verifier = null) =>
        (verifier ?? Verifier()).VerifyAsync(header, Bytes(body ?? _body));

    // ---------- допустимое ----------

    [Fact]
    public async Task A_correctly_signed_webhook_is_accepted()
    {
        Assert.True(await Verify(Sign()));
    }

    [Fact]
    public async Task Non_ascii_text_in_the_body_is_hashed_as_utf8()
    {
        var body = LiveKitBodies.ParticipantJoined("EV_u", "dev-room", "bob-1", "Бобби 🙂");

        Assert.True(await Verify(Sign(body), body));
    }

    [Fact]
    public async Task Spaces_around_the_header_value_are_tolerated()
    {
        Assert.True(await Verify("  " + Sign() + " "));
    }

    // ---------- нет подписи или нечем проверить ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("a.b.c")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.e30.")]
    public async Task A_missing_or_garbage_header_is_rejected(string? header)
    {
        Assert.False(await Verify(header));
    }

    [Fact]
    public async Task A_bearer_prefix_is_rejected_because_livekit_sends_the_bare_token()
    {
        Assert.False(await Verify("Bearer " + Sign()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short-secret-31-characters!!")]   // 31 символ: на один меньше минимума
    public async Task Without_a_proper_secret_nothing_is_accepted_not_even_a_valid_looking_token(string? configuredSecret)
    {
        var token = Sign();   // подписан настоящим секретом

        Assert.False(await Verify(token, verifier: Verifier(secret: configuredSecret)));
    }

    // ---------- подпись и издатель ----------

    [Fact]
    public async Task A_token_signed_with_another_secret_is_rejected()
    {
        var forged = Sign(options: new LiveKitSigner.Options(Secret: "another-secret-another-secret-another-secret"));

        Assert.False(await Verify(forged));
    }

    [Fact]
    public async Task A_token_from_another_key_is_rejected()
    {
        Assert.False(await Verify(Sign(options: new LiveKitSigner.Options(Issuer: "someone-else"))));
    }

    [Fact]
    public async Task The_configured_key_name_is_what_counts()
    {
        var token = Sign(options: new LiveKitSigner.Options(Issuer: "prod-key"));

        Assert.True(await Verify(token, verifier: Verifier(apiKey: "prod-key")));
        Assert.False(await Verify(token));   // с ключом по умолчанию не проходит
    }

    [Fact]
    public async Task A_single_changed_character_in_the_signature_is_rejected()
    {
        var token = Sign();
        var last = token[^1] == 'A' ? 'B' : 'A';

        Assert.False(await Verify(token[..^1] + last));
    }

    // ---------- алгоритм ----------

    [Fact]
    public async Task An_unsigned_token_with_alg_none_is_rejected()
    {
        Assert.False(await Verify(LiveKitSigner.Unsigned(_body, _clock.GetUtcNow().UtcDateTime)));
    }

    [Theory]
    [InlineData(SecurityAlgorithms.HmacSha384)]
    [InlineData(SecurityAlgorithms.HmacSha512)]
    public async Task Only_hs256_is_accepted_even_with_the_right_secret(string algorithm)
    {
        var token = LiveKitSigner.SignWithAlgorithm(_body, _clock.GetUtcNow().UtcDateTime, algorithm);

        Assert.False(await Verify(token));   // подпись настоящая, но алгоритм не тот
    }

    // ---------- срок жизни ----------

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        var token = Sign();

        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(31));   // срок 5 минут + допуск 30 секунд

        Assert.False(await Verify(token));
    }

    [Fact]
    public async Task A_token_just_past_its_expiry_is_still_tolerated_within_the_clock_skew()
    {
        var token = Sign();

        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(20));

        Assert.True(await Verify(token));
    }

    [Fact]
    public async Task A_token_that_is_not_valid_yet_is_rejected_beyond_the_skew()
    {
        Assert.False(await Verify(Sign(options: new LiveKitSigner.Options(NotBeforeOffset: TimeSpan.FromMinutes(2)))));
        Assert.True(await Verify(Sign(options: new LiveKitSigner.Options(NotBeforeOffset: TimeSpan.FromSeconds(20)))));
    }

    [Fact]
    public async Task A_token_without_an_expiry_is_rejected()
    {
        Assert.False(await Verify(Sign(options: new LiveKitSigner.Options(OmitExpiry: true))));
    }

    // ---------- хеш тела ----------

    [Fact]
    public async Task A_token_signed_for_another_body_is_rejected()
    {
        var other = LiveKitBodies.RoomFinished("EV_other", "dev-room");

        Assert.False(await Verify(Sign(other), _body));   // перехваченную подпись приложили к чужому телу
    }

    [Fact]
    public async Task A_body_changed_after_signing_is_rejected()
    {
        var token = Sign();

        Assert.False(await Verify(token, _body.Replace("alice-1", "alice-2")));
        Assert.False(await Verify(token, _body + "\n"));   // даже лишний перевод строки
        Assert.False(await Verify(token, _body[..^1]));
    }

    [Fact]
    public async Task A_token_without_the_body_hash_is_rejected()
    {
        Assert.False(await Verify(Sign(options: new LiveKitSigner.Options(OmitHash: true))));
    }

    [Fact]
    public async Task A_hash_in_another_encoding_is_rejected()
    {
        var hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(_body)));

        Assert.True(await Verify(Sign()));                      // base64 — как у LiveKit — принимается
        Assert.False(await Verify(SignWithRawHash(hex)));       // тот же хеш в шестнадцатеричном виде — нет
    }

    [Fact]
    public async Task An_empty_body_with_a_matching_hash_passes_the_signature_check()
    {
        // Подпись верна; что тело пустое — забота разбора (он вернёт «не событие»), а не проверки подписи.
        Assert.True(await Verify(Sign(""), ""));
    }

    private string SignWithRawHash(string rawHash)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = ApiFactory.LiveKitApiKey,
            NotBefore = _clock.GetUtcNow().UtcDateTime,
            Expires = _clock.GetUtcNow().UtcDateTime.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sha256"] = rawHash },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256),
        };
        return new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(descriptor);
    }
}
