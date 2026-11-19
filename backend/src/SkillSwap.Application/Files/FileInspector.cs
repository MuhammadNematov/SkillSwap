using System.IO.Compression;
using System.Text;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Files;

/// <summary>Файл, прошедший проверку: тип определён по содержимому, имя безопасное.</summary>
public sealed record InspectedFile(AttachmentKind Kind, string ContentType, string FileName);

/// <summary>
/// Проверка загружаемых файлов (раздел 10.1 ТЗ, «вредоносные файлы»): белый список типов, сверка расширения с содержимым (magic bytes),
/// размеры, безопасное имя. Тип сообщает не клиент (его <c>Content-Type</c> не используется), а содержимое.
/// Белый список: pdf, docx, xlsx, pptx, zip, txt, png, jpg, webp и голосовое WebM/Opus.
/// </summary>
public static class FileInspector
{
    public const long MaxFileBytes = 20L * 1024 * 1024;
    public const long MaxVoiceBytes = 4L * 1024 * 1024;
    public const int MaxVoiceSeconds = 120;

    /// <summary>Картинки с огромным числом пикселей раскодировать нельзя: файл в мегабайт может занять гигабайты памяти.</summary>
    public const long MaxImagePixels = 25_000_000;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = "application/pdf",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["zip"] = "application/zip",
        ["txt"] = "text/plain",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["webp"] = "image/webp",
        ["webm"] = "audio/webm",
    };

    /// <summary>Максимальный размер для расширения (для проверки до чтения тела): голосовые меньше обычных файлов.</summary>
    public static long MaxBytesFor(string fileName) =>
        string.Equals(ExtensionOf(fileName), "webm", StringComparison.OrdinalIgnoreCase) ? MaxVoiceBytes : MaxFileBytes;

    public static InspectedFile Inspect(string? rawFileName, byte[] data, IImageProcessor images)
    {
        var fileName = SafeFileName(rawFileName);
        var extension = ExtensionOf(fileName);
        if (extension is null || !Types.TryGetValue(extension, out var contentType))
        {
            throw new BadRequestException("unsupported_file_type", "Этот тип файла не поддерживается. Можно: pdf, docx, xlsx, pptx, zip, txt, png, jpg, webp и голосовое WebM.");
        }

        if (data.Length == 0)
        {
            throw new BadRequestException("file_empty", "Файл пустой.");
        }

        var kind = extension.ToLowerInvariant() switch
        {
            "png" or "jpg" or "jpeg" or "webp" => AttachmentKind.Image,
            "webm" => AttachmentKind.Voice,
            _ => AttachmentKind.File,
        };

        var limit = kind == AttachmentKind.Voice ? MaxVoiceBytes : MaxFileBytes;
        if (data.Length > limit)
        {
            throw new PayloadTooLargeException("file_too_large", $"Файл больше {limit / (1024 * 1024)} МБ.");
        }

        var matches = extension.ToLowerInvariant() switch
        {
            "pdf" => StartsWith(data, "%PDF-"u8),
            "docx" => ZipHasEntry(data, "word/document.xml"),
            "xlsx" => ZipHasEntry(data, "xl/workbook.xml"),
            "pptx" => ZipHasEntry(data, "ppt/presentation.xml"),
            "zip" => StartsWith(data, [0x50, 0x4B, 0x03, 0x04]) || StartsWith(data, [0x50, 0x4B, 0x05, 0x06]),
            "txt" => LooksLikeText(data),
            "png" => ImageMatches(data, images, ImageFormat.Png),
            "jpg" or "jpeg" => ImageMatches(data, images, ImageFormat.Jpeg),
            "webp" => ImageMatches(data, images, ImageFormat.Webp),
            "webm" => IsWebmOpus(data),
            _ => false,
        };

        if (!matches)
        {
            throw new BadRequestException("file_type_mismatch", "Содержимое файла не соответствует его расширению.");
        }

        return new InspectedFile(kind, contentType, fileName);
    }

    /// <summary>Имя без путей и управляющих символов, не длиннее 100 знаков (расширение сохраняется). Пустое имя недопустимо.</summary>
    public static string SafeFileName(string? raw)
    {
        var name = raw ?? string.Empty;
        name = name[(Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\')) + 1)..];
        var builder = new StringBuilder();
        foreach (var c in name)
        {
            if (!char.IsControl(c) && "\"<>:|?*".IndexOf(c) < 0)
            {
                builder.Append(c);
            }
        }

        name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0 || name.StartsWith('.'))
        {
            throw new BadRequestException("invalid_file_name", "Недопустимое имя файла.");
        }

        if (name.Length > Attachment.FileNameMaxLength)
        {
            var dot = name.LastIndexOf('.');
            var extension = dot > 0 && name.Length - dot <= 10 ? name[dot..] : string.Empty;
            name = name[..(Attachment.FileNameMaxLength - extension.Length)] + extension;
        }

        return name;
    }

    private static string? ExtensionOf(string? fileName)
    {
        var dot = fileName?.LastIndexOf('.') ?? -1;
        return dot > 0 && dot < fileName!.Length - 1 ? fileName[(dot + 1)..] : null;
    }

    private static bool StartsWith(byte[] data, ReadOnlySpan<byte> prefix) => data.AsSpan().StartsWith(prefix);

    private static bool ImageMatches(byte[] data, IImageProcessor images, ImageFormat expected)
    {
        var probe = images.Probe(data);
        if (probe is null || probe.Value.Format != expected)
        {
            return false;
        }

        if ((long)probe.Value.Width * probe.Value.Height > MaxImagePixels)
        {
            throw new BadRequestException("image_too_large", "Картинка слишком большая по размерам (не больше 25 мегапикселей).");
        }

        return true;
    }

    /// <summary>docx, xlsx и pptx — это zip-архивы с обязательной частью; настоящий zip с таким расширением без неё не принимается.</summary>
    private static bool ZipHasEntry(byte[] data, string entry)
    {
        if (!StartsWith(data, [0x50, 0x4B, 0x03, 0x04]))
        {
            return false;
        }

        try
        {
            using var archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            return archive.Entries.Any(e => string.Equals(e.FullName, entry, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>Текст: без нулевых байтов и в допустимой UTF-8 (с BOM или без).</summary>
    private static bool LooksLikeText(byte[] data)
    {
        if (Array.IndexOf(data, (byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Контейнер WebM (EBML) с аудиодорожкой Opus; кодек указан в заголовке в начале файла.</summary>
    private static bool IsWebmOpus(byte[] data)
    {
        if (!StartsWith(data, [0x1A, 0x45, 0xDF, 0xA3]))
        {
            return false;
        }

        var head = data.AsSpan(0, Math.Min(data.Length, 8192));
        return head.IndexOf("A_OPUS"u8) >= 0;
    }
}
