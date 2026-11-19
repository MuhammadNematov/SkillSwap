namespace SkillSwap.Application.Abstractions;

/// <summary>Картинка после обработки: метаданных (EXIF, GPS, XMP, комментарии) в ней нет.</summary>
public sealed record ProcessedImage(byte[] Bytes, string ContentType, int Width, int Height);

public enum ImageFormat
{
    Png,
    Jpeg,
    Webp,
}

/// <summary>Обработка картинок: удаление метаданных и уменьшение (аватары и картинки в чате).</summary>
public interface IImageProcessor
{
    /// <summary>
    /// Определяет формат по содержимому и размеры, не раскодируя весь файл. Null — это не поддерживаемая картинка (png, jpeg, webp).
    /// </summary>
    (ImageFormat Format, int Width, int Height)? Probe(ReadOnlySpan<byte> data);

    /// <summary>
    /// Раскодирует и закодирует заново в том же формате: все метаданные отбрасываются, поворот из EXIF применяется к пикселям.
    /// Не уменьшает и не меняет формат.
    /// </summary>
    ProcessedImage StripMetadata(byte[] data);

    /// <summary>
    /// Аватар: обрезка до квадрата по центру, уменьшение до <paramref name="size"/> пикселей по стороне (меньшие не увеличиваются),
    /// формат WebP, метаданных нет.
    /// </summary>
    ProcessedImage MakeAvatar(byte[] data, int size);
}
