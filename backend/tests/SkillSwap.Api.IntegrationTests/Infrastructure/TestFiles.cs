using System.IO.Compression;
using System.Text;
using SkiaSharp;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>Образцы файлов для тестов загрузки: настоящие картинки и документы, а также «ловушки» с метаданными.</summary>
public static class TestFiles
{
    /// <summary>Строка, которой в чистом файле быть не должно: ею помечены метаданные.</summary>
    public const string Marker = "SECRET-GPS-MARKER-7788";

    /// <summary>Картинка с цветными углами: левый верхний угол красный, остальные синие, чтобы видеть поворот.</summary>
    public static SKBitmap Picture(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);
        using var red = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(0, 0, width / 4f, height / 4f, red);
        return bitmap;
    }

    public static byte[] Png(int width = 64, int height = 48)
    {
        using var bitmap = Picture(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    public static byte[] Jpeg(int width = 64, int height = 48)
    {
        using var bitmap = Picture(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    public static byte[] Webp(int width = 64, int height = 48)
    {
        using var bitmap = Picture(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Webp, 90).ToArray();
    }

    /// <summary>JPEG с блоком EXIF (ориентация и «геоданные» с меткой) и комментарием: именно такие метаданные нужно убирать.</summary>
    public static byte[] JpegWithExif(int width, int height, int orientation)
    {
        var jpeg = Jpeg(width, height);
        var description = Encoding.ASCII.GetBytes(Marker + "\0");

        // TIFF: заголовок, IFD0 из двух записей (Orientation, ImageDescription), данные описания после IFD.
        using var tiff = new MemoryStream();
        using var w = new BinaryWriter(tiff);
        w.Write(new byte[] { 0x49, 0x49, 0x2A, 0x00 });   // "II", 42
        w.Write(8u);                                        // смещение IFD0
        w.Write((ushort)2);
        w.Write((ushort)0x0112); w.Write((ushort)3); w.Write(1u); w.Write((ushort)orientation); w.Write((ushort)0);
        w.Write((ushort)0x010E); w.Write((ushort)2); w.Write((uint)description.Length); w.Write(38u);
        w.Write(0u);                                        // следующего IFD нет
        w.Write(description);

        var app1Payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff.ToArray()).ToArray();
        var comment = Encoding.ASCII.GetBytes("COMMENT-" + Marker);

        using var result = new MemoryStream();
        result.Write(jpeg, 0, 2);   // SOI
        WriteSegment(result, 0xE1, app1Payload);
        WriteSegment(result, 0xFE, comment);
        result.Write(jpeg, 2, jpeg.Length - 2);
        return result.ToArray();
    }

    private static void WriteSegment(Stream stream, byte marker, byte[] payload)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(marker);
        var length = payload.Length + 2;
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)length);
        stream.Write(payload);
    }

    /// <summary>PNG с текстовым блоком tEXt (так хранят комментарии и описания).</summary>
    public static byte[] PngWithText(int width = 64, int height = 48)
    {
        var png = Png(width, height);
        var data = Encoding.ASCII.GetBytes("Comment\0" + Marker);
        var chunkBody = Encoding.ASCII.GetBytes("tEXt").Concat(data).ToArray();

        using var result = new MemoryStream();
        result.Write(png, 0, 33);   // сигнатура (8) и IHDR (25)
        var length = (uint)data.Length;
        result.Write(new[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length });
        result.Write(chunkBody);
        var crc = Crc32(chunkBody);
        result.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc });
        result.Write(png, 33, png.Length - 33);
        return result.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    public static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");

    /// <summary>Zip-архив с указанными записями.</summary>
    public static byte[] Zip(params string[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("<xml/>");
            }
        }

        return stream.ToArray();
    }

    public static byte[] Docx() => Zip("[Content_Types].xml", "word/document.xml");

    public static byte[] Xlsx() => Zip("[Content_Types].xml", "xl/workbook.xml");

    public static byte[] Pptx() => Zip("[Content_Types].xml", "ppt/presentation.xml");

    /// <summary>Заголовок WebM (EBML) с аудиодорожкой Opus; настоящий поток не нужен, достаточно того, что видит проверка сигнатур.</summary>
    public static byte[] WebmOpus(int extraBytes = 2000)
    {
        var bytes = new List<byte> { 0x1A, 0x45, 0xDF, 0xA3 };
        bytes.AddRange(new byte[] { 0x9F, 0x42, 0x86, 0x81, 0x01 });
        bytes.AddRange(Encoding.ASCII.GetBytes("webm"));
        bytes.AddRange(Encoding.ASCII.GetBytes("A_OPUS"));
        bytes.AddRange(new byte[extraBytes]);
        return bytes.ToArray();
    }

    /// <summary>Тот же контейнер, но с видеокодеком: голосовым считаться не должен.</summary>
    public static byte[] WebmVideo()
    {
        var bytes = new List<byte> { 0x1A, 0x45, 0xDF, 0xA3 };
        bytes.AddRange(Encoding.ASCII.GetBytes("webmV_VP8"));
        bytes.AddRange(new byte[500]);
        return bytes.ToArray();
    }

    public static byte[] Text(string text = "Привет, это заметка.") => Encoding.UTF8.GetBytes(text);

    public static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;
}
