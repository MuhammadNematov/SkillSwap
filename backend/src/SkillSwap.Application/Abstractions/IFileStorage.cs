namespace SkillSwap.Application.Abstractions;

/// <summary>Объект, прочитанный из хранилища.</summary>
public sealed record StoredFile(Stream Content, string ContentType, long Length);

/// <summary>
/// Хранилище файлов (раздел 10.1 ТЗ): закрытый бакет, доступ по протоколу S3. Приложение не знает, Garage это или облачное S3.
/// </summary>
public interface IFileStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct);

    /// <summary>Читает объект; null, если его нет. Поток нужно освободить.</summary>
    Task<StoredFile?> GetAsync(string key, CancellationToken ct);

    /// <summary>Удаляет объект; если его нет, ничего не происходит.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>
    /// Подписанная ссылка на скачивание: <paramref name="lifetime"/> действует, браузер сохраняет файл под именем
    /// <paramref name="fileName"/> (<c>Content-Disposition: attachment</c>). Строится без обращения к сети.
    /// </summary>
    string CreateDownloadUrl(string key, string fileName, string contentType, TimeSpan lifetime);

    /// <summary>Подписанная ссылка для показа картинки на странице (<c>Content-Disposition: inline</c>), например аватара. Без обращения к сети.</summary>
    string CreateViewUrl(string key, string contentType, TimeSpan lifetime);
}
