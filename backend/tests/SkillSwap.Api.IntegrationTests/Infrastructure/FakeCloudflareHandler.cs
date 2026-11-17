using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Заглушка сервера Cloudflare Turnstile (siteverify) вместо настоящих обращений в интернет.
/// Подставляется в HttpClient приложения, поэтому настоящий код клиента (формирование запроса, разбор ответа,
/// обработка сбоев) выполняется целиком; подделан только сам сервер.
/// <para>
/// Поведение выбирается по значению токена:
/// <c>pass…</c> — успех, но один раз (как у Cloudflare токен одноразовый: повтор даёт <c>timeout-or-duplicate</c>);
/// <c>outage</c> — сеть недоступна; <c>slow</c> — не отвечает (упирается в таймаут); <c>http-500</c> — сервер Cloudflare упал;
/// <c>garbage</c> — ответ не JSON; <c>wrong-secret</c> / <c>internal</c> — ошибки не по вине пользователя;
/// <c>unknown-code</c> — незнакомый код ошибки; <c>empty-object</c> — «{}»; всё остальное — <c>invalid-input-response</c>.
/// </para>
/// Как и настоящий сервер, сверяет секрет с ожидаемым.
/// </summary>
public sealed class FakeCloudflareHandler : HttpMessageHandler
{
    public sealed record Call(Uri Url, HttpMethod Method, string? ContentType, IReadOnlyDictionary<string, string> Form);

    private readonly ConcurrentQueue<Call> _calls = new();
    private readonly ConcurrentDictionary<string, bool> _spent = new();

    /// <summary>Секрет, который «знает» сервер. Запрос с другим секретом получает invalid-input-secret.</summary>
    public string ExpectedSecret { get; set; } = "1x0000000000000000000000000000000AA";

    public IReadOnlyList<Call> Calls => _calls.ToArray();

    /// <summary>Новый токен, который «решит» капчу (один раз).</summary>
    public static string PassToken() => "pass-" + Guid.NewGuid().ToString("N");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var parsed = HttpUtility.ParseQueryString(body);
        var form = parsed.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => parsed[k]!);
        _calls.Enqueue(new Call(request.RequestUri!, request.Method, request.Content?.Headers.ContentType?.MediaType, form));

        var token = form.GetValueOrDefault("response", string.Empty);

        if (form.GetValueOrDefault("secret") != ExpectedSecret)
        {
            return Json(false, "invalid-input-secret");
        }

        switch (token)
        {
            case "outage":
                throw new HttpRequestException("Нет соединения с Cloudflare (заглушка).");
            case "slow":
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);   // пока не сработает таймаут клиента
                break;
            case "http-500":
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            case "garbage":
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not json</html>", Encoding.UTF8, "text/html") };
            case "wrong-secret":
                return Json(false, "invalid-input-secret");
            case "internal":
                return Json(false, "internal-error");
            case "unknown-code":
                return Json(false, "something-new");
            case "empty-object":
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }

        if (token.StartsWith("pass", StringComparison.Ordinal))
        {
            return _spent.TryAdd(token, true)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"success":true,"challenge_ts":"2026-11-17T12:00:00Z","hostname":"localhost","error-codes":[],"action":"test","cdata":"test"}""",
                        Encoding.UTF8, "application/json"),
                }
                : Json(false, "timeout-or-duplicate");
        }

        return Json(false, "invalid-input-response");
    }

    private static HttpResponseMessage Json(bool success, params string[] errors) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"success":{{(success ? "true" : "false")}},"error-codes":[{{string.Join(",", errors.Select(e => $"\"{e}\""))}}]}""",
                Encoding.UTF8, "application/json"),
        };
}
