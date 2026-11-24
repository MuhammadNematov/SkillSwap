using Microsoft.Extensions.Options;
using SkillSwap.Application.Options;
using SkillSwap.Infrastructure.Storage;

namespace SkillSwap.Api.IntegrationTests.Files;

/// <summary>Storage:RelativeUrls: ссылки на файлы без адреса хранилища, чтобы они работали с любого адреса сайта (localhost, адрес в сети) без перенастройки.</summary>
public class RelativeUrlsTests
{
    private static S3FileStorage Storage(bool relative) => new(Options.Create(new StorageOptions
    {
        Endpoint = "http://localhost:3900", AccessKey = "key", SecretKey = "secret", Bucket = "skillswap", RelativeUrls = relative,
    }));

    private static IEnumerable<string> ParameterNames(string query) => query.TrimStart('?').Split('&').Select(p => p.Split('=')[0]).Order();

    [Fact]
    public void A_view_link_is_a_path_with_the_signature_and_no_address()
    {
        using var storage = Storage(relative: true);

        var url = storage.CreateViewUrl("avatars/a/b.webp", "image/webp", TimeSpan.FromHours(1));

        Assert.StartsWith("/skillswap/avatars/a/b.webp?", url);
        Assert.DoesNotContain("://", url);
        Assert.Contains("X-Amz-Signature=", url);
        Assert.Contains("X-Amz-SignedHeaders=host", url);
    }

    [Fact]
    public void A_download_link_is_relative_too_and_keeps_the_file_name_override()
    {
        using var storage = Storage(relative: true);

        var url = storage.CreateDownloadUrl("files/x.pdf", "Отчёт.pdf", "application/pdf", TimeSpan.FromMinutes(5));

        Assert.StartsWith("/skillswap/files/x.pdf?", url);
        Assert.Contains("response-content-disposition=", url);
        Assert.DoesNotContain("://", url);
    }

    [Fact]
    public void Without_the_mode_the_link_stays_absolute()
    {
        using var storage = Storage(relative: false);

        var url = storage.CreateViewUrl("avatars/a/b.webp", "image/webp", TimeSpan.FromHours(1));

        Assert.StartsWith("http://localhost:3900/skillswap/avatars/a/b.webp?", url);
    }

    [Fact]
    public void The_relative_link_is_the_absolute_one_without_the_address()
    {
        using var absolute = Storage(relative: false);
        using var relative = Storage(relative: true);

        var a = new Uri(absolute.CreateViewUrl("avatars/a/b.webp", "image/webp", TimeSpan.FromHours(1)));
        var r = relative.CreateViewUrl("avatars/a/b.webp", "image/webp", TimeSpan.FromHours(1));

        Assert.StartsWith(a.AbsolutePath + "?", r);   // тот же путь, подписано для того же внутреннего адреса
        Assert.Equal(ParameterNames(a.Query), ParameterNames(r[r.IndexOf('?')..]));   // тот же набор параметров подписи
    }
}
