using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;

namespace SkillSwap.Infrastructure.Storage;

/// <summary>Хранилище не настроено (разработка без Garage): всё, что требует файлов, отвечает 503 с понятным кодом.</summary>
public sealed class UnavailableFileStorage : IFileStorage
{
    private static ServiceUnavailableException Unavailable() => new(
        "storage_not_configured",
        "Хранилище файлов не настроено. Поднимите Garage из docker-compose и задайте Storage:Endpoint, Storage:AccessKey, Storage:SecretKey.");

    public Task PutAsync(string key, Stream content, string contentType, CancellationToken ct) => throw Unavailable();

    public Task<StoredFile?> GetAsync(string key, CancellationToken ct) => throw Unavailable();

    public Task DeleteAsync(string key, CancellationToken ct) => throw Unavailable();

    public string CreateDownloadUrl(string key, string fileName, string contentType, TimeSpan lifetime) => throw Unavailable();

    public string CreateViewUrl(string key, string contentType, TimeSpan lifetime) => throw Unavailable();
}
