using Microsoft.Extensions.Logging;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Chat;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Files;

/// <summary>
/// Вложения чата (раздел 7.1 ТЗ): загрузка файла или голосового как сообщения и выдача подписанной ссылки на 5 минут.
/// Файл проверяется (белый список, сигнатуры, размер), картинки очищаются от метаданных; хранится в закрытом бакете S3.
/// </summary>
public sealed class AttachmentService
{
    /// <summary>Сколько действует ссылка на скачивание (раздел 10.1 ТЗ).</summary>
    public static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(5);

    private readonly ChatService _chat;
    private readonly IMessageStore _messages;
    private readonly IFileStorage _storage;
    private readonly IImageProcessor _images;
    private readonly TimeProvider _clock;
    private readonly ILogger<AttachmentService> _logger;

    public AttachmentService(
        ChatService chat, IMessageStore messages, IFileStorage storage, IImageProcessor images, TimeProvider clock, ILogger<AttachmentService> logger)
    {
        _chat = chat;
        _messages = messages;
        _storage = storage;
        _images = images;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Загрузить файл в чат. Для голосового (<c>.webm</c>, Opus) обязательна длительность <paramref name="durationSeconds"/> 1–120.
    /// Создаётся сообщение типа File или Voice, его получает собеседник так же, как текст.
    /// </summary>
    public async Task<MessageDto> UploadAsync(Guid userId, Guid exchangeId, UploadedFile file, int? durationSeconds, CancellationToken ct)
    {
        var exchange = await _chat.AuthorizeSendAsync(userId, exchangeId, ct);

        // Размер проверяется до чтения тела: огромный файл не загружается в память.
        var limit = FileInspector.MaxBytesFor(FileInspector.SafeFileName(file.FileName));
        if (file.Length > limit)
        {
            throw new PayloadTooLargeException("file_too_large", $"Файл больше {limit / (1024 * 1024)} МБ.");
        }

        var data = await ReadAllAsync(file.Content, limit, ct);
        var inspected = FileInspector.Inspect(file.FileName, data, _images);

        int? duration = null;
        if (inspected.Kind == AttachmentKind.Voice)
        {
            if (durationSeconds is not { } seconds || seconds < 1)
            {
                throw new BadRequestException("invalid_duration", "Для голосового сообщения укажите длительность в секундах.");
            }

            if (seconds > FileInspector.MaxVoiceSeconds)
            {
                throw new BadRequestException("voice_too_long", $"Голосовое сообщение не длиннее {FileInspector.MaxVoiceSeconds} секунд.");
            }

            duration = seconds;
        }

        var contentType = inspected.ContentType;
        if (inspected.Kind == AttachmentKind.Image)
        {
            // Метаданные (EXIF, GPS, XMP) в картинках чата не сохраняем (раздел 10.1 ТЗ).
            try
            {
                var clean = _images.StripMetadata(data);
                data = clean.Bytes;
                contentType = clean.ContentType;
            }
            catch (InvalidDataException)
            {
                throw new BadRequestException("image_corrupt", "Не удалось прочитать картинку: файл повреждён.");
            }
        }

        var attachment = Attachment.Create(userId, exchangeId, inspected.Kind, inspected.FileName, contentType, data.Length, duration);
        var message = Message.CreateAttachment(
            exchangeId, userId, inspected.Kind == AttachmentKind.Voice ? MessageType.Voice : MessageType.File, attachment.Id);

        await using (var stream = new MemoryStream(data, writable: false))
        {
            await _storage.PutAsync(attachment.StorageKey, stream, contentType, ct);
        }

        try
        {
            _messages.AddAttachment(attachment);
            _messages.Add(message);
            await _messages.SaveAsync(ct);
        }
        catch
        {
            await TryDeleteAsync(attachment.StorageKey);   // запись не сохранилась: файл в хранилище осиротел
            throw;
        }

        var dto = _chat.ToMessageDto(message, AttachmentMapper.ToDto(attachment));
        await _chat.PublishAsync(message, dto, exchange, userId, ct);
        return dto;
    }

    /// <summary>
    /// Подписанная ссылка на скачивание на 5 минут. Только участникам обмена, пока чат им доступен; вложение удалённого сообщения
    /// недоступно. Чужое и несуществующее — 404 <c>attachment_not_found</c>.
    /// </summary>
    public async Task<AttachmentUrlDto> GetUrlAsync(Guid userId, Guid attachmentId, CancellationToken ct)
    {
        var attachment = await _messages.FindAttachmentAsync(attachmentId, ct)
                         ?? throw new NotFoundException("attachment_not_found", "Файл не найден.");

        try
        {
            await _chat.EnsureCanJoinAsync(userId, attachment.ExchangeId, ct);   // участник, чат не в архиве
        }
        catch (NotFoundException)
        {
            throw new NotFoundException("attachment_not_found", "Файл не найден.");   // чужой обмен выглядит как отсутствие файла
        }

        var message = await _messages.FindByAttachmentAsync(attachmentId, ct);
        if (message is null || message.IsDeleted)
        {
            throw new NotFoundException("attachment_not_found", "Файл не найден.");
        }

        var url = _storage.CreateDownloadUrl(attachment.StorageKey, attachment.FileName, attachment.MimeType, UrlLifetime);
        // Срок подписи ссылки считает SDK по системным часам: ту же отметку отдаём клиенту.
        return new AttachmentUrlDto(url, DateTime.UtcNow + UrlLifetime - TimeSpan.FromSeconds(1), attachment.FileName);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, long limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                throw new PayloadTooLargeException("file_too_large", $"Файл больше {limit / (1024 * 1024)} МБ.");   // длину в заголовке можно подделать
            }
        }

        return buffer.ToArray();
    }

    private async Task TryDeleteAsync(string key)
    {
        try
        {
            await _storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить осиротевший файл {Key}", key);
        }
    }
}
