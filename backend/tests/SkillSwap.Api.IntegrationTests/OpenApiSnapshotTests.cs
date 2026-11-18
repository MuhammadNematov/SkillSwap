using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SkillSwap.Api.IntegrationTests.Infrastructure;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>
/// Контракт API для фронтенда лежит в <c>docs/openapi.json</c> и порождается самим приложением. Этот тест сравнивает файл
/// с тем, что приложение отдаёт сейчас (<c>/openapi/v1.json</c> в Development): если API изменился, а файл нет, тест падает,
/// и CI красный. Обновить файл: <c>UPDATE_OPENAPI=1 dotnet test --filter OpenApiSnapshotTests</c> и закоммитить результат.
/// </summary>
[Collection(ApiCollection.Name)]
public class OpenApiSnapshotTests
{
    private readonly ApiFactory _factory;

    public OpenApiSnapshotTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_committed_openapi_file_matches_the_application()
    {
        using var app = _factory.WithSettings("Development");
        var generated = Normalize(await app.CreateClient().GetStringAsync("/openapi/v1.json"));
        var path = SnapshotPath();

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, generated, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), $"Нет файла {path}. Создайте его: UPDATE_OPENAPI=1 dotnet test --filter OpenApiSnapshotTests");

        var committed = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n");
        Assert.True(committed == generated,
            "docs/openapi.json устарел: API изменился, а файл нет. Обновите его командой " +
            "UPDATE_OPENAPI=1 dotnet test --filter OpenApiSnapshotTests (PowerShell: $env:UPDATE_OPENAPI='1') и закоммитьте. " +
            FirstDifference(committed, generated));
    }

    [Fact]
    public async Task The_snapshot_is_stable_between_two_generations()
    {
        using var app = _factory.WithSettings("Development");
        var client = app.CreateClient();

        var first = Normalize(await client.GetStringAsync("/openapi/v1.json"));
        var second = Normalize(await client.GetStringAsync("/openapi/v1.json"));

        Assert.Equal(first, second);   // иначе проверка актуальности краснела бы без причины
    }

    [Fact]
    public async Task The_snapshot_has_no_environment_specific_or_secret_values()
    {
        using var app = _factory.WithSettings("Development");
        var generated = await app.CreateClient().GetStringAsync("/openapi/v1.json");

        foreach (var forbidden in new[] { ApiFactory.LiveKitApiSecret, ApiFactory.TurnstileTestSecret, "Jwt", "ConnectionStrings", "Password=", "/dev/livekit" })
        {
            Assert.DoesNotContain(forbidden, generated);
        }
    }

    /// <summary>Одинаковая запись при любом окружении: отступы, переводы строк, кириллица без экранирования.</summary>
    private static string Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            document.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    private static string FirstDifference(string committed, string generated)
    {
        var a = committed.Split('\n');
        var b = generated.Split('\n');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return $"Первое отличие в строке {i + 1}: в файле «{a[i].Trim()}», в приложении «{b[i].Trim()}».";
            }
        }

        return $"Файл и приложение отличаются длиной: {a.Length} и {b.Length} строк.";
    }

    /// <summary>docs/openapi.json в корне репозитория (поднимаемся от папки сборки до каталога, где лежат docs и backend).</summary>
    private static string SnapshotPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "docs")) && Directory.Exists(Path.Combine(dir.FullName, "backend")))
            {
                return Path.Combine(dir.FullName, "docs", "openapi.json");
            }
        }

        throw new InvalidOperationException("Не найден корень репозитория (каталоги docs и backend).");
    }
}
