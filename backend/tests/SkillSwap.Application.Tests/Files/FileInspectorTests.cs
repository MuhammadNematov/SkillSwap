using System.IO.Compression;
using System.Text;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Files;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Tests.Files;

public class FileInspectorTests
{
    /// <summary>Подставной разбор картинок: узнаёт PNG по сигнатуре и «размеры» берёт из первых байтов, настоящие картинки проверяются в интеграционных тестах.</summary>
    private sealed class FakeImages : IImageProcessor
    {
        public (ImageFormat Format, int Width, int Height)? Probe(ReadOnlySpan<byte> data)
        {
            if (data.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }))
            {
                return (ImageFormat.Png, data[^2] * 1000, data[^1] * 1000);   // последние два байта «задают» размеры в тысячах
            }

            return data.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }) ? (ImageFormat.Jpeg, 10, 10) : null;
        }

        public ProcessedImage StripMetadata(byte[] data) => throw new NotSupportedException();

        public ProcessedImage MakeAvatar(byte[] data, int size) => throw new NotSupportedException();
    }

    private static readonly FakeImages Images = new();

    private static byte[] Png(byte w = 1, byte h = 1) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, w, h];

    private static byte[] Zip(params string[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var e in entries)
            {
                using var w = new StreamWriter(archive.CreateEntry(e).Open());
                w.Write("x");
            }
        }

        return stream.ToArray();
    }

    private static byte[] WebmOpus() => [0x1A, 0x45, 0xDF, 0xA3, .. "xxA_OPUSyy"u8.ToArray()];

    [Theory]
    [InlineData("a.pdf", "application/pdf", AttachmentKind.File)]
    [InlineData("A.PDF", "application/pdf", AttachmentKind.File)]
    [InlineData("v.webm", "audio/webm", AttachmentKind.Voice)]
    public void Valid_files_get_their_type_from_the_extension_and_content(string name, string mime, AttachmentKind kind)
    {
        var data = name.EndsWith("webm", StringComparison.OrdinalIgnoreCase) ? WebmOpus() : "%PDF-1.4 body"u8.ToArray();

        var result = FileInspector.Inspect(name, data, Images);

        Assert.Equal((kind, mime), (result.Kind, result.ContentType));
    }

    [Fact]
    public void An_image_is_inspected_through_the_image_processor()
    {
        var result = FileInspector.Inspect("p.png", Png(), Images);

        Assert.Equal(AttachmentKind.Image, result.Kind);
        Assert.Equal("image/png", result.ContentType);
    }

    [Fact]
    public void A_jpeg_with_a_png_extension_is_a_mismatch()
    {
        var ex = Assert.Throws<BadRequestException>(() => FileInspector.Inspect("p.png", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0], Images));

        Assert.Equal("file_type_mismatch", ex.Code);
    }

    [Fact]
    public void Too_many_pixels_are_refused_before_decoding()
    {
        var ex = Assert.Throws<BadRequestException>(() => FileInspector.Inspect("p.png", Png(6, 6), Images));   // 6000 x 6000 = 36 мегапикселей

        Assert.Equal("image_too_large", ex.Code);
        FileInspector.Inspect("p.png", Png(5, 5), Images);   // ровно 25 мегапикселей допустимо
    }

    [Theory]
    [InlineData("a.exe")]
    [InlineData("a")]
    [InlineData("a.")]
    [InlineData("a.pdf.exe")]
    [InlineData("a.svg")]
    [InlineData("a.html")]
    public void Unknown_extensions_are_refused(string name)
    {
        var ex = Assert.Throws<BadRequestException>(() => FileInspector.Inspect(name, [1, 2, 3], Images));

        Assert.Equal("unsupported_file_type", ex.Code);
    }

    [Fact]
    public void An_empty_file_is_refused() =>
        Assert.Equal("file_empty", Assert.Throws<BadRequestException>(() => FileInspector.Inspect("a.pdf", [], Images)).Code);

    [Fact]
    public void Office_documents_need_their_required_part()
    {
        FileInspector.Inspect("a.docx", Zip("word/document.xml"), Images);
        FileInspector.Inspect("a.xlsx", Zip("xl/workbook.xml"), Images);
        FileInspector.Inspect("a.pptx", Zip("ppt/presentation.xml"), Images);
        FileInspector.Inspect("a.zip", Zip("anything.txt"), Images);

        Assert.Equal("file_type_mismatch", Assert.Throws<BadRequestException>(() => FileInspector.Inspect("a.docx", Zip("x.txt"), Images)).Code);
        Assert.Equal("file_type_mismatch", Assert.Throws<BadRequestException>(() => FileInspector.Inspect("a.xlsx", Zip("word/document.xml"), Images)).Code);
        Assert.Equal("file_type_mismatch", Assert.Throws<BadRequestException>(() => FileInspector.Inspect("a.docx", "PK\u0003\u0004 broken"u8.ToArray(), Images)).Code);
    }

    [Fact]
    public void Text_must_be_valid_utf8_without_nul_bytes()
    {
        FileInspector.Inspect("a.txt", Encoding.UTF8.GetBytes("Привет, мир"), Images);
        FileInspector.Inspect("a.txt", [0xEF, 0xBB, 0xBF, 0x68, 0x69], Images);   // с BOM

        Assert.Throws<BadRequestException>(() => FileInspector.Inspect("a.txt", [0x68, 0x00, 0x69], Images));
        Assert.Throws<BadRequestException>(() => FileInspector.Inspect("a.txt", [0xC3, 0x28], Images));
    }

    [Fact]
    public void Webm_must_carry_the_opus_codec()
    {
        Assert.Equal("file_type_mismatch", Assert.Throws<BadRequestException>(
            () => FileInspector.Inspect("v.webm", [0x1A, 0x45, 0xDF, 0xA3, .. "V_VP9"u8.ToArray()], Images)).Code);
        Assert.Throws<BadRequestException>(() => FileInspector.Inspect("v.webm", "A_OPUS but no EBML header"u8.ToArray(), Images));
    }

    [Fact]
    public void Size_limits_differ_for_files_and_voice()
    {
        Assert.Equal(20L * 1024 * 1024, FileInspector.MaxBytesFor("a.pdf"));
        Assert.Equal(4L * 1024 * 1024, FileInspector.MaxBytesFor("v.webm"));
        Assert.Equal(4L * 1024 * 1024, FileInspector.MaxBytesFor("V.WEBM"));

        var voice = new byte[4 * 1024 * 1024 + 1];
        WebmOpus().CopyTo(voice, 0);
        Assert.Equal("file_too_large", Assert.Throws<PayloadTooLargeException>(() => FileInspector.Inspect("v.webm", voice, Images)).Code);
    }

    [Theory]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData(@"..\..\evil.txt", "evil.txt")]
    [InlineData("C:\\dir\\file.txt", "file.txt")]
    [InlineData("a<b>c:d|e?f*g.txt", "abcdefg.txt")]
    [InlineData("tab\tand\nnewline\u0000.txt", "tabandnewline.txt")]
    [InlineData("  spaced name .txt  ", "spaced name .txt")]
    [InlineData("Мой отчёт 2026.pdf", "Мой отчёт 2026.pdf")]
    public void File_names_are_made_safe(string raw, string expected) => Assert.Equal(expected, FileInspector.SafeFileName(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("///")]
    public void Names_that_leave_nothing_are_refused(string? raw) =>
        Assert.Equal("invalid_file_name", Assert.Throws<BadRequestException>(() => FileInspector.SafeFileName(raw)).Code);

    [Fact]
    public void A_long_name_is_cut_to_100_characters_keeping_the_extension()
    {
        var name = FileInspector.SafeFileName(new string('x', 300) + ".docx");

        Assert.Equal(100, name.Length);
        Assert.EndsWith(".docx", name);
    }
}
