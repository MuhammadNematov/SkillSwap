using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;
using SkillSwap.Infrastructure.LiveKit;

namespace SkillSwap.Api.IntegrationTests.LiveKit;

/// <summary>Удаление комнаты через API LiveKit (раздел 7.2 ТЗ): адрес, токен, ответы сервера и сбои.</summary>
public class LiveKitRoomServiceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static LiveKitRoomService Create(Handler handler, Action<LiveKitOptions>? configure = null)
    {
        var options = new LiveKitOptions { Url = "ws://livekit.test:7880", ApiKey = ApiFactory.LiveKitApiKey, ApiSecret = ApiFactory.LiveKitApiSecret };
        configure?.Invoke(options);
        var wrapped = Options.Create(options);
        var tokens = new LiveKitTokenService(wrapped, new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero)));
        return new LiveKitRoomService(new Factory(handler), tokens, wrapped, NullLogger<LiveKitRoomService>.Instance);
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("{}") };

    [Fact]
    public async Task The_room_is_deleted_by_a_signed_twirp_call_to_the_http_address_made_from_the_websocket_one()
    {
        var handler = new Handler(_ => Status(HttpStatusCode.OK));

        await Create(handler).DeleteRoomAsync("session-abc", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal("http://livekit.test:7880/twirp/livekit.RoomService/DeleteRoom", handler.Last.RequestUri!.ToString());
        Assert.Equal("session-abc", JsonDocument.Parse(handler.LastBody!).RootElement.GetProperty("room").GetString());

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(handler.Last.Headers.Authorization!.Parameter!, new TokenValidationParameters
        {
            ValidIssuer = ApiFactory.LiveKitApiKey,
            ValidateAudience = false,
            ValidateLifetime = false,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.LiveKitApiSecret)),
        });
        Assert.True(result.IsValid);
        var video = ((JsonWebToken)result.SecurityToken).GetPayloadValue<JsonElement>("video");
        Assert.True(video.GetProperty("roomCreate").GetBoolean());
        Assert.Equal("session-abc", video.GetProperty("room").GetString());
        Assert.False(video.TryGetProperty("roomJoin", out _));   // серверный токен не пускает в комнату
        var token = (JsonWebToken)result.SecurityToken;
        Assert.Equal(60, (token.ValidTo - token.ValidFrom).TotalSeconds);
    }

    [Fact]
    public async Task A_secure_websocket_address_becomes_https_and_an_explicit_api_address_wins()
    {
        var secure = new Handler(_ => Status(HttpStatusCode.OK));
        await Create(secure, o => o.Url = "wss://video.example.com").DeleteRoomAsync("r", CancellationToken.None);
        Assert.Equal("https://video.example.com/twirp/livekit.RoomService/DeleteRoom", secure.Last!.RequestUri!.ToString());

        var explicitUrl = new Handler(_ => Status(HttpStatusCode.OK));
        await Create(explicitUrl, o => o.ApiUrl = "http://livekit-internal:7880").DeleteRoomAsync("r", CancellationToken.None);
        Assert.Equal("http://livekit-internal:7880/twirp/livekit.RoomService/DeleteRoom", explicitUrl.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_room_that_is_already_gone_is_not_an_error()
    {
        await Create(new Handler(_ => Status(HttpStatusCode.NotFound))).DeleteRoomAsync("r", CancellationToken.None);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Any_other_answer_means_unavailable_so_the_job_will_retry(HttpStatusCode code)
    {
        await Assert.ThrowsAsync<LiveKitUnavailableException>(
            () => Create(new Handler(_ => Status(code))).DeleteRoomAsync("r", CancellationToken.None));
    }

    [Fact]
    public async Task A_network_failure_means_unavailable()
    {
        var handler = new Handler(_ => throw new HttpRequestException("no route"));

        await Assert.ThrowsAsync<LiveKitUnavailableException>(() => Create(handler).DeleteRoomAsync("r", CancellationToken.None));
    }

    [Fact]
    public void Without_a_secret_the_service_says_it_is_not_configured()
    {
        Assert.False(Create(new Handler(_ => Status(HttpStatusCode.OK)), o => o.ApiSecret = null).IsConfigured);
        Assert.True(Create(new Handler(_ => Status(HttpStatusCode.OK))).IsConfigured);
    }
}
