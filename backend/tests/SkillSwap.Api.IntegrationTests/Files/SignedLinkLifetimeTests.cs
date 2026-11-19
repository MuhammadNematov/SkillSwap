using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Options;

namespace SkillSwap.Api.IntegrationTests.Files;

/// <summary>
/// Срок подписанной ссылки остаётся в пределах ±5 секунд от заявленного при любой «запомненной» SDK поправке часов от −2 до +2 секунд.
/// SDK хранит эту поправку в одном словаре на весь процесс (по адресу хранилища) и обновляет её по заголовку Date каждого ответа, а он точен до секунды.
/// Поэтому X-Amz-Expires отличается от заявленного на секунды в зависимости от того, какой запрос к хранилищу шёл перед ссылкой: раньше тесты со
/// строгим сроком падали только в полном прогоне. Стандартное поведение SDK не меняем (поправка нужна, если часы сервера уйдут), допуск в тестах.
/// </summary>
[Collection(ApiCollection.Name)]
public class SignedLinkLifetimeTests
{
    private readonly ApiFactory _factory;

    public SignedLinkLifetimeTests(ApiFactory factory)
    {
        _factory = factory;
    }

    /// <summary>Подкладывает SDK «запомненную» поправку часов для адреса хранилища (как после ответа сервера с отстающим или спешащим Date).</summary>
    private static void Remember(string endpoint, TimeSpan correction)
    {
        var type = typeof(Amazon.Runtime.AmazonServiceClient).Assembly.GetType("Amazon.Runtime.CorrectClockSkew")!;
        var set = type.GetMethod("SetClockCorrectionForEndpoint", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        set.Invoke(null, [endpoint, correction]);
    }

    [Theory]
    [InlineData(-2000)]
    [InlineData(-1000)]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(2000)]
    public void A_remembered_clock_skew_of_up_to_two_seconds_keeps_the_link_lifetime_within_five_seconds(int skewMilliseconds)
    {
        _ = _factory.CreateClient();   // поднимает приложение и контейнер хранилища
        using var scope = _factory.Services.CreateScope();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var endpoint = scope.ServiceProvider.GetRequiredService<IOptions<StorageOptions>>().Value.Endpoint!.TrimEnd('/');

        // Ключ в словаре SDK — адрес сервиса (с путём бакета или без): кладём под все виды, чтобы проверка не зависела от формата.
        foreach (var key in new[] { endpoint, endpoint + "/", endpoint + "/skillswap-test/" })
        {
            Remember(key, TimeSpan.FromMilliseconds(skewMilliseconds));
        }

        try
        {
            SignedLinks.AssertLifetime(
                storage.CreateDownloadUrl("exchanges/x/y", "a.pdf", "application/pdf", TimeSpan.FromMinutes(5)), TimeSpan.FromMinutes(5));
            SignedLinks.AssertLifetime(storage.CreateViewUrl("avatars/x/y.webp", "image/webp", TimeSpan.FromHours(1)), TimeSpan.FromHours(1));
        }
        finally
        {
            foreach (var key in new[] { endpoint, endpoint + "/", endpoint + "/skillswap-test/" })
            {
                Remember(key, TimeSpan.Zero);   // не оставляем подложенную поправку другим тестам
            }
        }
    }
}
