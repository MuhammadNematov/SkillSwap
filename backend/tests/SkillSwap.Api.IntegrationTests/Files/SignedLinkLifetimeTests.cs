using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;

namespace SkillSwap.Api.IntegrationTests.Files;

/// <summary>
/// Срок подписанной ссылки не зависит от общего состояния AWS SDK. SDK хранит поправку на расхождение часов в одном словаре на весь процесс
/// (по адресу хранилища) и обновляет её по заголовку Date каждого ответа, а он точен до секунды. Раньше из-за этого X-Amz-Expires у ссылки
/// «плавал» на секунды в зависимости от того, какой запрос к хранилищу шёл перед ней, и тесты со сроком ссылки падали только в полном прогоне.
/// </summary>
[Collection(ApiCollection.Name)]
public class SignedLinkLifetimeTests
{
    private readonly ApiFactory _factory;

    public SignedLinkLifetimeTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Подкладывает SDK «запомненную» поправку часов для адреса хранилища (как после ответа сервера с отстающим Date).</summary>
    private static void Remember(string endpoint, TimeSpan correction)
    {
        var type = typeof(Amazon.Runtime.AmazonServiceClient).Assembly.GetType("Amazon.Runtime.CorrectClockSkew")!;
        var set = type.GetMethod("SetClockCorrectionForEndpoint", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        set.Invoke(null, [endpoint, correction]);
    }

    private static int ExpiresOf(string url) => int.Parse(Regex.Match(url, @"X-Amz-Expires=(\d+)").Groups[1].Value);

    [Theory]
    [InlineData(-1800)]
    [InlineData(-1000)]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(1900)]
    public void A_remembered_clock_skew_does_not_change_how_long_the_link_lives(int skewMilliseconds)
    {
        _ = _factory.CreateClient();   // поднимает приложение и контейнер хранилища
        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var endpoint = scope.ServiceProvider.GetRequiredService<IOptions<StorageOptions>>().Value.Endpoint!.TrimEnd('/');

        // Адрес в словаре SDK — это адрес сервиса с путём бакета; кладём поправку под оба вида ключа, чтобы проверка не зависела от формата.
        foreach (var key in new[] { endpoint, endpoint + "/", endpoint + "/skillswap-test/" })
        {
            Remember(key, TimeSpan.FromMilliseconds(skewMilliseconds));
        }

        var fiveMinutes = storage.CreateDownloadUrl("exchanges/x/y", "a.pdf", "application/pdf", TimeSpan.FromMinutes(5));
        var oneHour = storage.CreateViewUrl("avatars/x/y.webp", "image/webp", TimeSpan.FromHours(1));

        Assert.InRange(ExpiresOf(fiveMinutes), 295, 300);
        Assert.InRange(ExpiresOf(oneHour), 3595, 3600);
    }
}
