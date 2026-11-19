using SkillSwap.Domain.Common;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Entities;

/// <summary>
/// Вложение сообщения чата (таблица Attachments, раздел 13 ТЗ): сам файл лежит в закрытом бакете S3 под ключом <see cref="StorageKey"/>,
/// здесь только описание. Имя файла безопасное (без путей и управляющих символов), тип определён сервером по содержимому.
/// </summary>
public class Attachment : BaseEntity
{
    public const int FileNameMaxLength = 100;
    public const int MimeTypeMaxLength = 100;

    private Attachment()
    {
    }

    public Guid OwnerId { get; private set; }
    public Guid ExchangeId { get; private set; }
    public AttachmentKind Kind { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string MimeType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }

    /// <summary>Длительность голосового в секундах (сообщает клиент, ограничена 120).</summary>
    public int? DurationSec { get; private set; }

    public static string KeyFor(Guid exchangeId, Guid attachmentId) => $"exchanges/{exchangeId:N}/{attachmentId:N}";

    public static Attachment Create(
        Guid ownerId, Guid exchangeId, AttachmentKind kind, string fileName, string mimeType, long sizeBytes, int? durationSec)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > FileNameMaxLength)
        {
            throw new DomainValidationException("invalid_file_name", "Недопустимое имя файла.");
        }

        if (sizeBytes <= 0)
        {
            throw new DomainValidationException("file_empty", "Файл пустой.");
        }

        if (kind == AttachmentKind.Voice && durationSec is not > 0)
        {
            throw new DomainValidationException("invalid_duration", "У голосового сообщения должна быть длительность.");
        }

        var attachment = new Attachment
        {
            OwnerId = ownerId, ExchangeId = exchangeId, Kind = kind, FileName = fileName, MimeType = mimeType,
            SizeBytes = sizeBytes, DurationSec = kind == AttachmentKind.Voice ? durationSec : null,
        };
        attachment.StorageKey = KeyFor(exchangeId, attachment.Id);
        return attachment;
    }
}
