using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SkillSwap.Api.IntegrationTests.RateLimiting;

internal static class RateLimitAssert
{
    /// <summary>
    /// 429 в едином формате: ProblemDetails с кодом rate_limited и заголовок Retry-After (целые секунды),
    /// причём значение в заголовке и в теле совпадает с ожидаемым.
    /// </summary>
    public static async Task RateLimitedAsync(HttpResponseMessage response, int retryAfterSeconds)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Equal(retryAfterSeconds, (int)response.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
        Assert.Equal(retryAfterSeconds.ToString(), response.Headers.GetValues("Retry-After").Single());   // именно целые секунды

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(429, json.GetProperty("status").GetInt32());
        Assert.Equal("rate_limited", json.GetProperty("code").GetString());
        Assert.Equal(retryAfterSeconds, json.GetProperty("retryAfterSeconds").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("title").GetString()));
        Assert.Contains(retryAfterSeconds.ToString(), json.GetProperty("detail").GetString());
        Assert.True(json.TryGetProperty("traceId", out _));
    }

    /// <summary>Не 429: запрос дошёл до обработчика (какой бы ответ он ни дал).</summary>
    public static void NotRateLimited(HttpResponseMessage response) =>
        Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
}
