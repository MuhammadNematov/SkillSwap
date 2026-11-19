using SkiaSharp;
using SkillSwap.Application.Abstractions;

namespace SkillSwap.Infrastructure.Storage;

/// <summary>
/// Обработка картинок на SkiaSharp (лицензия MIT). Картинка раскодируется и кодируется заново: всё, что не пиксели (EXIF, GPS, XMP,
/// комментарии), не переносится в результат. Поворот из EXIF применяется к самим пикселям, иначе после удаления метаданных
/// фотография с телефона легла бы набок.
/// </summary>
public sealed class SkiaImageProcessor : IImageProcessor
{
    private const int JpegQuality = 92;
    private const int WebpQuality = 92;
    private const int AvatarWebpQuality = 85;

    public (ImageFormat Format, int Width, int Height)? Probe(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            return null;
        }

        using var stream = new SKMemoryStream(data.ToArray());
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            return null;
        }

        var format = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => (ImageFormat?)ImageFormat.Png,
            SKEncodedImageFormat.Jpeg => ImageFormat.Jpeg,
            SKEncodedImageFormat.Webp => ImageFormat.Webp,
            _ => null,
        };

        return format is { } f ? (f, codec.Info.Width, codec.Info.Height) : null;
    }

    public ProcessedImage StripMetadata(byte[] data)
    {
        using var oriented = DecodeOriented(data, out var format);
        var (encoded, contentType) = format switch
        {
            SKEncodedImageFormat.Png => (Encode(oriented, SKEncodedImageFormat.Png, 100), "image/png"),
            SKEncodedImageFormat.Jpeg => (Encode(oriented, SKEncodedImageFormat.Jpeg, JpegQuality), "image/jpeg"),
            SKEncodedImageFormat.Webp => (Encode(oriented, SKEncodedImageFormat.Webp, WebpQuality), "image/webp"),
            _ => throw new InvalidDataException("Формат картинки не поддерживается."),
        };

        return new ProcessedImage(encoded, contentType, oriented.Width, oriented.Height);
    }

    public ProcessedImage MakeAvatar(byte[] data, int size)
    {
        using var oriented = DecodeOriented(data, out _);

        // Квадрат по центру из меньшей стороны.
        var side = Math.Min(oriented.Width, oriented.Height);
        var left = (oriented.Width - side) / 2;
        var top = (oriented.Height - side) / 2;
        using var square = new SKBitmap(side, side, oriented.ColorType, oriented.AlphaType);
        using (var canvas = new SKCanvas(square))
        {
            canvas.DrawBitmap(oriented, new SKRect(left, top, left + side, top + side), new SKRect(0, 0, side, side), new SKSamplingOptions(SKFilterMode.Linear));
        }

        var target = Math.Min(side, size);   // маленькие картинки не увеличиваем
        using var scaled = target == side ? square.Copy() : Resize(square, target);
        return new ProcessedImage(Encode(scaled, SKEncodedImageFormat.Webp, AvatarWebpQuality), "image/webp", target, target);
    }

    private static SKBitmap Resize(SKBitmap source, int target)
    {
        var resized = source.Resize(new SKImageInfo(target, target, source.ColorType, source.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return resized ?? throw new InvalidDataException("Не удалось изменить размер картинки.");
    }

    private static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality) ?? throw new InvalidDataException("Не удалось закодировать картинку.");
        return data.ToArray();
    }

    private static SKBitmap DecodeOriented(byte[] data, out SKEncodedImageFormat format)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Файл не является поддерживаемой картинкой.");
        format = codec.EncodedFormat;
        var origin = codec.EncodedOrigin;

        var bitmap = SKBitmap.Decode(codec) ?? throw new InvalidDataException("Не удалось раскодировать картинку.");
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default)
        {
            return bitmap;
        }

        using (bitmap)
        {
            return ApplyOrigin(bitmap, origin);
        }
    }

    /// <summary>Поворот и отражение по значению ориентации EXIF (1–8).</summary>
    private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        float w = source.Width, h = source.Height;
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swaps ? source.Height : source.Width, swaps ? source.Width : source.Height, source.ColorType, source.AlphaType);

        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };

        using var canvas = new SKCanvas(result);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return result;
    }
}
