using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;

namespace SkillSwap.Infrastructure.LiveKit;

/// <summary>
/// Удаление комнаты через API LiveKit (Twirp: <c>POST /twirp/livekit.RoomService/DeleteRoom</c>, токен с правом roomCreate на эту комнату).
/// Комнаты нет (404) — успех. Любой другой сбой — <see cref="LiveKitUnavailableException"/>: задача закрытия комнаты повторится.
/// </summary>
public sealed class LiveKitRoomService : ILiveKitRoomService
{
    public const string HttpClientName = "livekit-api";

    private readonly IHttpClientFactory _httpClients;
    private readonly ILiveKitTokenService _tokens;
    private readonly LiveKitOptions _options;
    private readonly ILogger<LiveKitRoomService> _logger;

    public LiveKitRoomService(
        IHttpClientFactory httpClients, ILiveKitTokenService tokens, IOptions<LiveKitOptions> options, ILogger<LiveKitRoomService> logger)
    {
        _httpClients = httpClients;
        _tokens = tokens;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured && BaseUri() is not null;

    public async Task DeleteRoomAsync(string room, CancellationToken ct)
    {
        var baseUri = BaseUri() ?? throw new InvalidOperationException("LiveKit не настроен: нет адреса API.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "twirp/livekit.RoomService/DeleteRoom"))
        {
            Content = JsonContent.Create(new { room }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.CreateRoomDeleteToken(room));

        try
        {
            using var response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Комнаты {Room} уже нет.", room);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new LiveKitUnavailableException($"LiveKit ответил {(int)response.StatusCode} на удаление комнаты.");
            }

            _logger.LogInformation("Комната {Room} удалена.", room);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException && !ct.IsCancellationRequested)
        {
            throw new LiveKitUnavailableException("LiveKit недоступен.", ex);
        }
    }

    /// <summary>Адрес API: явный <c>ApiUrl</c> или <c>Url</c> с заменой ws на http и wss на https.</summary>
    private Uri? BaseUri()
    {
        var raw = !string.IsNullOrWhiteSpace(_options.ApiUrl) ? _options.ApiUrl : _options.Url;
        raw = raw.Trim()
            .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase);
        if (!raw.EndsWith('/'))
        {
            raw += "/";
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
    }
}
