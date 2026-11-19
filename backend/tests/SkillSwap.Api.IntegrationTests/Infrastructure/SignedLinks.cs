using System.Text.RegularExpressions;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Проверка срока подписанной ссылки. SDK считает X-Amz-Expires с поправкой на расхождение часов с хранилищем, которую обновляет по заголовку Date
/// каждого ответа (точность секунда), поэтому срок отличается от заявленного на секунды. Для пользователя это не важно, допуск ±5 секунд.
/// </summary>
public static class SignedLinks
{
    public const int ToleranceSeconds = 5;

    public static int ExpiresIn(string url) => int.Parse(Regex.Match(url, @"X-Amz-Expires=(\d+)").Groups[1].Value);

    public static void AssertLifetime(string url, TimeSpan expected)
    {
        Assert.Contains("X-Amz-Signature=", url);
        var seconds = (int)expected.TotalSeconds;
        Assert.InRange(ExpiresIn(url), seconds - ToleranceSeconds, seconds + ToleranceSeconds);
    }
}
