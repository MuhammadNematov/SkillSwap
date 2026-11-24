using SkillSwap.Application.Options;
using SkillSwap.Infrastructure.Storage;

namespace SkillSwap.Api.IntegrationTests.Files;

/// <summary>Настройки хранилища проверяются при старте: вне разработки без настоящего хранилища приложение не запускается.</summary>
public class StorageStartupCheckTests
{
    private static StorageOptions Valid() => new()
    {
        Endpoint = "https://s3.example.com", AccessKey = "key", SecretKey = "secret", Bucket = "skillswap", Region = "eu-central-1",
    };

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Without_storage_development_and_tests_still_start(string environment) =>
        StorageStartupCheck.Validate(new StorageOptions(), isDevelopmentLike: true, environment);

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Without_storage_other_environments_do_not_start(string environment)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StorageStartupCheck.Validate(new StorageOptions(), isDevelopmentLike: false, environment));

        Assert.Contains("Storage:Endpoint", ex.Message);
        Assert.Contains("Garage", ex.Message);
    }

    [Fact]
    public void A_proper_production_configuration_is_accepted() => StorageStartupCheck.Validate(Valid(), isDevelopmentLike: false, "Production");

    [Fact]
    public void Local_garage_over_http_is_fine_for_development()
    {
        var options = new StorageOptions { Endpoint = "http://localhost:3900", AccessKey = "k", SecretKey = "s" };

        StorageStartupCheck.Validate(options, isDevelopmentLike: true, "Development");
    }

    [Fact]
    public void A_remote_http_endpoint_is_refused_in_production()
    {
        var options = Valid();
        options.Endpoint = "http://s3.example.com";

        var ex = Assert.Throws<InvalidOperationException>(() => StorageStartupCheck.Validate(options, isDevelopmentLike: false, "Production"));
        Assert.Contains("https", ex.Message);
    }

    [Fact]
    public void A_loopback_http_endpoint_is_allowed_in_production_for_a_storage_on_the_same_host()
    {
        var options = Valid();
        options.Endpoint = "http://127.0.0.1:3900";

        StorageStartupCheck.Validate(options, isDevelopmentLike: false, "Production");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://s3.example.com")]
    [InlineData("s3.example.com")]
    public void A_nonsense_endpoint_is_refused(string endpoint)
    {
        var options = Valid();
        options.Endpoint = endpoint;

        Assert.Throws<InvalidOperationException>(() => StorageStartupCheck.Validate(options, isDevelopmentLike: false, "Production"));
    }

    [Fact]
    public void A_nonsense_public_endpoint_is_refused()
    {
        var options = Valid();
        options.PublicEndpoint = "files.example.com";

        Assert.Throws<InvalidOperationException>(() => StorageStartupCheck.Validate(options, isDevelopmentLike: false, "Production"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Relative_urls_are_accepted_in_development_and_tests(string environment)
    {
        var options = Valid();
        options.RelativeUrls = true;

        StorageStartupCheck.Validate(options, isDevelopmentLike: true, environment);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Relative_urls_outside_development_do_not_start_with_a_clear_error(string environment)
    {
        var options = Valid();
        options.RelativeUrls = true;

        var ex = Assert.Throws<InvalidOperationException>(() => StorageStartupCheck.Validate(options, isDevelopmentLike: false, environment));

        Assert.Contains(environment, ex.Message);
        Assert.Contains("Storage:RelativeUrls", ex.Message);
        Assert.Contains("только для разработки", ex.Message);
        Assert.Contains("Storage:PublicEndpoint", ex.Message);   // подсказка, что задать вместо
    }

    [Fact]
    public void Relative_urls_are_switched_on_only_in_the_development_settings_file()
    {
        static bool? Flag(string file)
        {
            var path = Path.Combine(AppContext.BaseDirectory, file);
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("Storage", out var storage) && storage.TryGetProperty("RelativeUrls", out var flag) ? flag.GetBoolean() : null;
        }

        Assert.Null(Flag("appsettings.json"));                 // общие настройки (они же боевые) режим не включают
        Assert.True(Flag("appsettings.Development.json"));     // включён только здесь
        Assert.Null(Flag("appsettings.Production.json"));      // если такой файл появится, режима в нём быть не должно
        Assert.Null(Flag("appsettings.Staging.json"));
    }

    [Fact]
    public void Relative_urls_together_with_a_public_endpoint_are_refused()
    {
        var options = Valid();
        options.RelativeUrls = true;
        options.PublicEndpoint = "https://files.example.com";

        var ex = Assert.Throws<InvalidOperationException>(() => StorageStartupCheck.Validate(options, isDevelopmentLike: true, "Development"));

        Assert.Contains("Storage:RelativeUrls", ex.Message);
        Assert.Contains("Storage:PublicEndpoint", ex.Message);
    }

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("key", null)]
    [InlineData("", "")]
    public void Keys_are_required_once_an_endpoint_is_set(string? access, string? secret)
    {
        var options = Valid();
        options.AccessKey = access;
        options.SecretKey = secret;

        var ex = Assert.Throws<InvalidOperationException>(() => StorageStartupCheck.Validate(options, isDevelopmentLike: true, "Development"));
        Assert.Contains("AccessKey", ex.Message);
    }

    [Fact]
    public void A_bucket_name_is_required()
    {
        var options = Valid();
        options.Bucket = " ";

        Assert.Throws<InvalidOperationException>(() => StorageStartupCheck.Validate(options, isDevelopmentLike: false, "Production"));
    }

    [Fact]
    public void Defaults_are_safe_and_aimed_at_garage()
    {
        var options = new StorageOptions();

        Assert.False(options.IsConfigured);
        Assert.Equal("garage", options.Region);
        Assert.True(options.ForcePathStyle);
        Assert.Null(options.SecretKey);   // секретов в коде нет
    }
}

/// <summary>Настоящее приложение: режим относительных ссылок только для разработки, в боевой среде оно с ним не запустится.</summary>
[Collection(SkillSwap.Api.IntegrationTests.Infrastructure.ApiCollection.Name)]
public class StorageRelativeUrlsStartupIntegrationTests
{
    private readonly SkillSwap.Api.IntegrationTests.Infrastructure.ApiFactory _factory;

    public StorageRelativeUrlsStartupIntegrationTests(SkillSwap.Api.IntegrationTests.Infrastructure.ApiFactory factory) => _factory = factory;

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            parts.Add(e.Message);
        }

        return string.Join(" | ", parts);
    }

    [Fact]
    public void The_application_refuses_to_start_in_production_with_relative_urls()
    {
        using var app = _factory.WithSettings("Production", ("Storage:RelativeUrls", "true"));

        var ex = Assert.ThrowsAny<Exception>(() => _ = app.Services);

        Assert.Contains("Storage:RelativeUrls", Flatten(ex));
    }

    [Fact]
    public void The_same_setting_is_fine_in_development()
    {
        using var app = _factory.WithSettings("Development", ("Storage:RelativeUrls", "true"));

        Assert.NotNull(app.Services);   // запустилось
    }
}
