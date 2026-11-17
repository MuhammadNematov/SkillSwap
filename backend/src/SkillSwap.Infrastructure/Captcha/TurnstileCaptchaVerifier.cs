using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;

namespace SkillSwap.Infrastructure.Captcha;

/// <summary>
/// Проверка токена Cloudflare Turnstile (siteverify). Любая неопределённость трактуется как «не пускать»:
/// если Cloudflare недоступен или ответил непонятно, результат <see cref="CaptchaOutcome.Unavailable"/>,
/// а не «пропустить» (иначе падение Cloudflare отключало бы защиту от ботов).
/// </summary>
public sealed class TurnstileCaptchaVerifier : ICaptchaVerifier
{
    public const string HttpClientName = "turnstile";

    /// <summary>Максимальная длина токена по документации Turnstile; длиннее — заведомо мусор, Cloudflare не беспокоим.</summary>
    public const int MaxTokenLength = 2048;

    // Ошибки, виновник которых — пользователь (токен плохой, просрочен или уже использован): можно решить капчу заново.
    private static readonly HashSet<string> UserErrors =
        ["missing-input-response", "invalid-input-response", "timeout-or-duplicate"];

    // Ошибки на нашей стороне или у Cloudflare: пользователь тут ни при чём, повторять бесполезно, нужно чинить.
    private static readonly HashSet<string> ServerErrors =
        ["missing-input-secret", "invalid-input-secret", "bad-request", "internal-error"];

    private readonly IHttpClientFactory _httpClients;
    private readonly TurnstileOptions _options;
    private readonly ILogger<TurnstileCaptchaVerifier> _logger;

    public TurnstileCaptchaVerifier(
        IHttpClientFactory httpClients,
        IOptions<TurnstileOptions> options,
        ILogger<TurnstileCaptchaVerifier> logger)
    {
        _httpClients = httpClients;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CaptchaVerification> VerifyAsync(string? token, string? remoteIp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return CaptchaVerification.Missing;
        }

        if (token.Length > MaxTokenLength)
        {
            return CaptchaVerification.Invalid("invalid-input-response");
        }

        var form = new Dictionary<string, string>
        {
            ["secret"] = _options.SecretKey ?? string.Empty,
            ["response"] = token,
        };
        if (!string.IsNullOrEmpty(remoteIp))
        {
            form["remoteip"] = remoteIp;
        }

        SiteVerifyResponse? answer;
        try
        {
            using var client = _httpClients.CreateClient(HttpClientName);
            using var response = await client.PostAsync(_options.VerifyUrl, new FormUrlEncodedContent(form), ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Cloudflare Turnstile ответил HTTP {Status}: проверка капчи недоступна", (int)response.StatusCode);
                return CaptchaVerification.Unavailable("http-" + (int)response.StatusCode);
            }

            answer = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // клиент оборвал запрос сам: это не сбой капчи
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            // TaskCanceledException без отмены от клиента — это истёкший таймаут HttpClient.
            _logger.LogError(ex, "Не удалось проверить капчу в Cloudflare Turnstile (недоступен, таймаут или непонятный ответ)");
            return CaptchaVerification.Unavailable("unreachable");
        }

        if (answer is null)
        {
            _logger.LogError("Cloudflare Turnstile вернул пустой ответ");
            return CaptchaVerification.Unavailable("empty-response");
        }

        if (answer.Success)
        {
            return CaptchaVerification.Passed;
        }

        var codes = (answer.ErrorCodes ?? []).ToArray();
        if (codes.Any(ServerErrors.Contains))
        {
            // Чаще всего это неверный секрет в нашей конфигурации: от этого страдают все пользователи сразу.
            _logger.LogError("Cloudflare Turnstile отклонил проверку по причине на нашей стороне: {Codes}", string.Join(", ", codes));
            return CaptchaVerification.Unavailable(codes);
        }

        if (codes.Length == 0 || !codes.All(UserErrors.Contains))
        {
            _logger.LogWarning("Cloudflare Turnstile вернул неизвестные коды ошибок: {Codes}", string.Join(", ", codes));
        }

        return CaptchaVerification.Invalid(codes);
    }

    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error-codes")] string[]? ErrorCodes);
}
