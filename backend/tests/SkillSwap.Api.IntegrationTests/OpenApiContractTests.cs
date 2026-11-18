using System.Text.Json;
using SkillSwap.Api.IntegrationTests.Infrastructure;

namespace SkillSwap.Api.IntegrationTests;

/// <summary>
/// Договор с фронтендом: каждый эндпоинт из раздела 12 ТЗ, реализованный к этому моменту, виден в OpenAPI (Swagger/Scalar)
/// с нужным методом. Тест падает, если эндпоинт пропал или переехал.
/// </summary>
[Collection(ApiCollection.Name)]
public class OpenApiContractTests
{
    private readonly ApiFactory _factory;

    public OpenApiContractTests(ApiFactory factory) => _factory = factory;

    private async Task<JsonElement> LoadAsync()
    {
        using var app = _factory.WithSettings("Development");
        var json = await app.CreateClient().GetStringAsync("/openapi/v1.json");
        return JsonDocument.Parse(json).RootElement;
    }

    public static TheoryData<string, string> Stage3Endpoints => new()
    {
        // Профиль
        { "get", "/api/v1/me" },
        { "put", "/api/v1/me" },
        { "get", "/api/v1/users/{id}" },
        { "post", "/api/v1/users/{id}/block" },
        { "delete", "/api/v1/users/{id}/block" },
        { "get", "/api/v1/me/blocks" },
        { "get", "/api/v1/languages" },
        // Навыки и анкета
        { "get", "/api/v1/skill-categories" },
        { "get", "/api/v1/skills" },
        { "get", "/api/v1/me/skills" },
        { "put", "/api/v1/me/skills" },
        { "post", "/api/v1/skills/suggestions" },
        // Подбор
        { "get", "/api/v1/matches" },
        // Каталог для админа
        { "get", "/api/v1/admin/skill-categories" },
        { "post", "/api/v1/admin/skill-categories" },
        { "get", "/api/v1/admin/skill-categories/{id}" },
        { "put", "/api/v1/admin/skill-categories/{id}" },
        { "delete", "/api/v1/admin/skill-categories/{id}" },
        { "get", "/api/v1/admin/skills" },
        { "post", "/api/v1/admin/skills" },
        { "get", "/api/v1/admin/skills/{id}" },
        { "put", "/api/v1/admin/skills/{id}" },
        { "delete", "/api/v1/admin/skills/{id}" },
    };

    [Theory]
    [MemberData(nameof(Stage3Endpoints))]
    public async Task Every_stage_3_endpoint_is_in_the_openapi_document(string method, string path)
    {
        var document = await LoadAsync();

        Assert.True(document.GetProperty("paths").TryGetProperty(path, out var item), $"в OpenAPI нет пути {path}");
        Assert.True(item.TryGetProperty(method, out var operation), $"у пути {path} нет метода {method.ToUpperInvariant()}");
        Assert.True(operation.TryGetProperty("tags", out var tags) && tags.GetArrayLength() > 0, $"{method.ToUpperInvariant()} {path} без тега (группы в Scalar)");
        Assert.True(operation.TryGetProperty("responses", out var responses) && responses.EnumerateObject().Any(), $"{method.ToUpperInvariant()} {path} без описания ответов");
    }

    [Fact]
    public async Task The_document_shows_the_error_responses_the_frontend_has_to_handle()
    {
        var document = await LoadAsync();
        var paths = document.GetProperty("paths");

        string[] CodesOf(string path, string method) =>
            paths.GetProperty(path).GetProperty(method).GetProperty("responses").EnumerateObject().Select(r => r.Name).ToArray();

        Assert.Contains("403", CodesOf("/api/v1/matches", "get"));            // skills_incomplete
        Assert.Contains("400", CodesOf("/api/v1/matches", "get"));
        Assert.Contains("409", CodesOf("/api/v1/skills/suggestions", "post")); // skill_already_exists, лимиты
        Assert.Contains("429", CodesOf("/api/v1/skills/suggestions", "post"));
        Assert.Contains("404", CodesOf("/api/v1/users/{id}", "get"));
        Assert.Contains("409", CodesOf("/api/v1/me/skills", "put"));
    }

    [Fact]
    public async Task The_document_declares_the_bearer_scheme_so_scalar_can_call_protected_endpoints()
    {
        var document = await LoadAsync();

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.True(document.GetProperty("security").GetArrayLength() > 0);
    }
}
