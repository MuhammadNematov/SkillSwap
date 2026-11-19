using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Options;

namespace SkillSwap.Infrastructure.Storage;

/// <summary>
/// Хранилище на протоколе S3 (Garage, а при смене настроек любое облачное S3). Два клиента: внутренний ходит по <c>Storage:Endpoint</c>,
/// публичный строит подписанные ссылки на адрес <c>Storage:PublicEndpoint</c>, который видит браузер (подпись зависит от адреса).
/// </summary>
public sealed class S3FileStorage : IFileStorage, IDisposable
{
    private readonly StorageOptions _options;
    private readonly AmazonS3Client _client;
    private readonly AmazonS3Client _publicClient;
    private readonly string _publicEndpoint;

    public S3FileStorage(IOptions<StorageOptions> options)
    {
        _options = options.Value;
        _publicEndpoint = string.IsNullOrWhiteSpace(_options.PublicEndpoint) ? _options.Endpoint! : _options.PublicEndpoint;
        _client = Create(_options, _options.Endpoint!);
        _publicClient = Create(_options, _publicEndpoint);
    }

    internal AmazonS3Client Client => _client;

    private static AmazonS3Client Create(StorageOptions options, string endpoint) => new(
        new BasicAWSCredentials(options.AccessKey, options.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = endpoint.TrimEnd('/'),
            ForcePathStyle = options.ForcePathStyle,
            AuthenticationRegion = options.Region,
            // Контрольные суммы только там, где без них нельзя: самохостные хранилища (Garage) не всегда их понимают.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
            MaxErrorRetry = options.MaxRetries,
        });

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        await Guard(() => _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, ct));
    }

    public async Task<StoredFile?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await _client.GetObjectAsync(_options.Bucket, key, ct);
            return new StoredFile(new ResponseStream(response), response.Headers.ContentType, response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException or TaskCanceledException or TimeoutException && !ct.IsCancellationRequested)
        {
            throw Unavailable(ex);
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct) =>
        Guard(() => _client.DeleteObjectAsync(_options.Bucket, key, ct));

    public string CreateDownloadUrl(string key, string fileName, string contentType, TimeSpan lifetime)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Protocol = _publicEndpoint.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP,   // по умолчанию SDK берёт https даже для http-адреса
            Expires = DateTime.UtcNow + lifetime - TimeSpan.FromSeconds(1),   // SDK округляет вверх: так ссылка живёт не больше заявленных 5 минут
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentDisposition = ContentDispositionFor(fileName),   // браузер сохраняет файл, а не открывает его
                ContentType = contentType,
            },
        };
        return _publicClient.GetPreSignedURL(request);
    }

    public string CreateViewUrl(string key, string contentType, TimeSpan lifetime) =>
        _publicClient.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Protocol = _publicEndpoint.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP,
            Expires = DateTime.UtcNow + lifetime - TimeSpan.FromSeconds(1),
            ResponseHeaderOverrides = new ResponseHeaderOverrides { ContentDisposition = "inline", ContentType = contentType },
        });

    /// <summary><c>attachment; filename="…"; filename*=UTF-8''…</c>: имя с кириллицей и пробелами доходит без искажений.</summary>
    internal static string ContentDispositionFor(string fileName)
    {
        var ascii = new string(fileName.Select(c => c is >= ' ' and <= '~' && c != '"' && c != '\\' && c != '%' ? c : '_').ToArray());
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    public void Dispose()
    {
        _client.Dispose();
        _publicClient.Dispose();
    }

    private static async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException or TaskCanceledException or TimeoutException)
        {
            throw Unavailable(ex);
        }
    }

    private static ServiceUnavailableException Unavailable(Exception inner) =>
        new("storage_unavailable", "Хранилище файлов временно недоступно. Повторите позже.");

    /// <summary>Поток ответа S3: освобождение потока освобождает и ответ (иначе соединение остаётся занятым).</summary>
    private sealed class ResponseStream : Stream
    {
        private readonly GetObjectResponse _response;
        private readonly Stream _inner;

        public ResponseStream(GetObjectResponse response)
        {
            _response = response;
            _inner = response.ResponseStream;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _response.ContentLength;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
