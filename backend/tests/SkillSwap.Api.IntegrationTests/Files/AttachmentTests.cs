using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Files;

/// <summary>Файлы и голосовые в чате (разделы 7.1 и 10.1 ТЗ): проверка типов и размеров, очистка картинок, подписанные ссылки, CORS.</summary>
[Collection(ApiCollection.Name)]
public class AttachmentTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _storageClient = new();   // ходит по подписанным ссылкам прямо в хранилище, как браузер

    public AttachmentTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static readonly (int, SkillLevel) Beginner0 = (0, SkillLevel.Beginner);
    private static readonly (int, SkillLevel) Beginner1 = (1, SkillLevel.Beginner);

    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B, Guid ExchangeId)> ExchangeAsync()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync(Beginner0, Beginner1);
        var b = await world.UserAsync(Beginner1, Beginner0);
        var created = await SendAsync(a, HttpMethod.Post, "/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var accept = await SendAsync(b, HttpMethod.Post, $"/exchange-requests/{requestId}/accept");
        return (world, a, b, (await JsonAsync(accept)).GetProperty("exchangeId").GetGuid());
    }

    private async Task<HttpResponseMessage> SendAsync(MatchPerson? who, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    private async Task<HttpResponseMessage> UploadAsync(MatchPerson? who, Guid exchangeId, byte[]? file, string fileName, int? durationSeconds = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/exchanges/{exchangeId}/attachments");
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        }

        var form = new MultipartFormDataContent { { new StringContent("x"), "other" } };
        if (file is not null)
        {
            var content = new ByteArrayContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");   // тип от клиента не доверяется
            form.Add(content, "file", fileName);
        }

        if (durationSeconds is { } d)
        {
            form.Add(new StringContent(d.ToString()), "durationSeconds");
        }

        request.Content = form;
        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> UploadOkAsync(MatchPerson who, Guid exchangeId, byte[] file, string fileName, int? duration = null)
    {
        var response = await UploadAsync(who, exchangeId, file, fileName, duration);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonAsync(response);
    }

    private async Task<(byte[] Bytes, HttpResponseMessage Response)> DownloadAsync(MatchPerson who, JsonElement message)
    {
        var id = message.GetProperty("attachment").GetProperty("id").GetGuid();
        var link = await SendAsync(who, HttpMethod.Get, $"/attachments/{id}/url");
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        var url = (await JsonAsync(link)).GetProperty("url").GetString()!;
        var response = await _storageClient.GetAsync(url);
        return (response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : [], response);
    }

    // ---------- файлы ----------

    [Theory]
    [InlineData("report.pdf", "application/pdf", "File")]
    [InlineData("essay.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "File")]
    [InlineData("table.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "File")]
    [InlineData("slides.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", "File")]
    [InlineData("pack.zip", "application/zip", "File")]
    [InlineData("notes.txt", "text/plain", "File")]
    [InlineData("pic.png", "image/png", "Image")]
    [InlineData("photo.jpg", "image/jpeg", "Image")]
    [InlineData("photo.jpeg", "image/jpeg", "Image")]
    [InlineData("pic.webp", "image/webp", "Image")]
    public async Task Every_whitelisted_type_is_accepted_and_stored(string name, string mime, string kind)
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var file = Path.GetExtension(name) switch
        {
            ".pdf" => TestFiles.Pdf(), ".docx" => TestFiles.Docx(), ".xlsx" => TestFiles.Xlsx(), ".pptx" => TestFiles.Pptx(),
            ".zip" => TestFiles.Zip("a.txt", "b/c.txt"), ".txt" => TestFiles.Text(), ".png" => TestFiles.Png(), ".webp" => TestFiles.Webp(),
            _ => TestFiles.Jpeg(),
        };

        var message = await UploadOkAsync(a, exchangeId, file, name);

        Assert.Equal("File", message.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, message.GetProperty("text").ValueKind);
        var attachment = message.GetProperty("attachment");
        Assert.Equal(name, attachment.GetProperty("fileName").GetString());
        Assert.Equal(mime, attachment.GetProperty("contentType").GetString());   // тип определён сервером, а не взят от клиента
        Assert.Equal(kind, attachment.GetProperty("kind").GetString());
        Assert.True(attachment.GetProperty("sizeBytes").GetInt64() > 0);
        Assert.Equal(a.Id, message.GetProperty("senderId").GetGuid());
        Assert.Equal(1, await _factory.QueryAsync(db => db.Attachments.CountAsync(x => x.ExchangeId == exchangeId)));

        var (bytes, response) = await DownloadAsync(b, message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (kind == "File")
        {
            Assert.Equal(file, bytes);   // обычные файлы лежат как есть
        }
    }

    [Fact]
    public async Task A_file_message_reaches_the_partner_through_the_hub_and_the_history_with_the_attachment()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var seen = new ConcurrentQueue<JsonElement>();
        await using var bConn = new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(b.Token);
            })
            .Build();
        bConn.On<JsonElement>("message", m => seen.Enqueue(m));
        await bConn.StartAsync();

        var sent = await UploadOkAsync(a, exchangeId, TestFiles.Pdf(), "договор.pdf");

        var until = DateTime.UtcNow.AddSeconds(15);
        while (seen.IsEmpty && DateTime.UtcNow < until)
        {
            await Task.Delay(40);
        }

        var pushed = Assert.Single(seen);
        Assert.Equal(sent.GetProperty("id").GetGuid(), pushed.GetProperty("id").GetGuid());
        Assert.Equal("договор.pdf", pushed.GetProperty("attachment").GetProperty("fileName").GetString());

        var history = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}/messages"))).GetProperty("items");
        Assert.Equal("договор.pdf", history[0].GetProperty("attachment").GetProperty("fileName").GetString());
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("text").ValueKind);

        // Сообщение с файлом — такое же сообщение: непрочитанное и с уведомлением.
        Assert.Equal(1, (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}"))).GetProperty("unreadMessages").GetInt32());
    }

    [Theory]
    [InlineData("virus.exe")]
    [InlineData("script.js")]
    [InlineData("page.html")]
    [InlineData("archive.rar")]
    [InlineData("noextension")]
    [InlineData("doc.pdf.exe")]
    [InlineData(".pdf")]
    public async Task A_type_outside_the_whitelist_is_refused(string name)
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();

        var response = await UploadAsync(a, exchangeId, TestFiles.Text("MZ-подобное содержимое"), name);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(await CodeAsync(response), new[] { "unsupported_file_type", "invalid_file_name" });
        Assert.Equal(0, await _factory.QueryAsync(db => db.Attachments.CountAsync(x => x.ExchangeId == exchangeId)));
    }

    [Fact]
    public async Task The_content_must_match_the_extension()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var exe = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 };   // заголовок исполняемого файла Windows

        foreach (var (file, name) in new (byte[], string)[]
        {
            (exe, "fake.pdf"), (exe, "fake.png"), (exe, "fake.jpg"), (exe, "fake.zip"), (exe, "fake.docx"),
            (TestFiles.Pdf(), "disguised.png"),                  // настоящий PDF под видом картинки
            (TestFiles.Png(), "disguised.pdf"),                  // и наоборот
            (TestFiles.Zip("a.txt"), "plain.docx"),              // zip без обязательной части документа
            (TestFiles.Docx(), "wrong.xlsx"),                    // документ другого вида
            (new byte[] { 1, 2, 3, 0, 4 }, "binary.txt"),        // нулевой байт: не текст
            ([0xFF, 0xFE, 0xFD, 0xC3, 0x28], "bad-utf8.txt"),    // недопустимая UTF-8
            (TestFiles.WebmVideo(), "video.webm"),               // WebM, но не Opus
        })
        {
            var response = await UploadAsync(a, exchangeId, file, name, 5);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("file_type_mismatch", await CodeAsync(response));
        }

        Assert.Equal(0, await _factory.QueryAsync(db => db.Attachments.CountAsync(x => x.ExchangeId == exchangeId)));
    }

    [Fact]
    public async Task An_empty_file_and_a_request_without_a_file_are_refused()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();

        var empty = await UploadAsync(a, exchangeId, [], "empty.txt");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("file_empty", await CodeAsync(empty));

        var none = await UploadAsync(a, exchangeId, null, "x.txt");
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("file_required", await CodeAsync(none));
    }

    [Fact]
    public async Task Dangerous_and_odd_file_names_are_made_safe()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var longName = new string('я', 150) + ".txt";

        foreach (var (given, expected) in new[]
        {
            ("../../etc/passwd.txt", "passwd.txt"),
            (@"..\..\windows\system32\evil.txt", "evil.txt"),
            ("bad<>:|?*name.txt", "badname.txt"),
            ("Мой отчёт 2026.txt", "Мой отчёт 2026.txt"),
        })
        {
            var message = await UploadOkAsync(a, exchangeId, TestFiles.Text(), given);
            Assert.Equal(expected, message.GetProperty("attachment").GetProperty("fileName").GetString());
        }

        var truncated = (await UploadOkAsync(a, exchangeId, TestFiles.Text(), longName)).GetProperty("attachment").GetProperty("fileName").GetString()!;
        Assert.Equal(100, truncated.Length);
        Assert.EndsWith(".txt", truncated);   // расширение сохраняется
    }

    [Fact]
    public async Task The_size_limits_are_20_megabytes_for_files_and_4_for_voice()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var twenty = new byte[20 * 1024 * 1024];
        Array.Fill(twenty, (byte)'a');

        var ok = await UploadAsync(a, exchangeId, twenty, "exactly-20mb.txt");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var tooBig = new byte[20 * 1024 * 1024 + 1];
        Array.Fill(tooBig, (byte)'a');
        var refused = await UploadAsync(a, exchangeId, tooBig, "over.txt");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
        Assert.Equal("file_too_large", await CodeAsync(refused));

        var voice = TestFiles.WebmOpus(4 * 1024 * 1024);   // чуть больше 4 МБ
        var voiceRefused = await UploadAsync(a, exchangeId, voice, "voice.webm", 30);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, voiceRefused.StatusCode);
        Assert.Equal(1, await _factory.QueryAsync(db => db.Attachments.CountAsync(x => x.ExchangeId == exchangeId)));   // осталась только первая
    }

    // ---------- голосовые ----------

    [Fact]
    public async Task A_voice_message_needs_a_duration_up_to_two_minutes()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();

        var ok = await UploadOkAsync(a, exchangeId, TestFiles.WebmOpus(), "voice.webm", 120);

        Assert.Equal("Voice", ok.GetProperty("type").GetString());
        Assert.Equal("Voice", ok.GetProperty("attachment").GetProperty("kind").GetString());
        Assert.Equal(120, ok.GetProperty("attachment").GetProperty("durationSeconds").GetInt32());
        Assert.Equal("audio/webm", ok.GetProperty("attachment").GetProperty("contentType").GetString());
        Assert.Equal(HttpStatusCode.OK, (await DownloadAsync(b, ok)).Response.StatusCode);

        var missing = await UploadAsync(a, exchangeId, TestFiles.WebmOpus(), "voice.webm");
        Assert.Equal("invalid_duration", await CodeAsync(missing));
        var zero = await UploadAsync(a, exchangeId, TestFiles.WebmOpus(), "voice.webm", 0);
        Assert.Equal("invalid_duration", await CodeAsync(zero));
        var tooLong = await UploadAsync(a, exchangeId, TestFiles.WebmOpus(), "voice.webm", 121);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("voice_too_long", await CodeAsync(tooLong));
    }

    [Fact]
    public async Task The_duration_is_ignored_for_ordinary_files()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();

        var message = await UploadOkAsync(a, exchangeId, TestFiles.Pdf(), "a.pdf", 5);

        Assert.Equal(JsonValueKind.Null, message.GetProperty("attachment").GetProperty("durationSeconds").ValueKind);
    }

    // ---------- картинки: метаданные ----------

    [Fact]
    public async Task Chat_images_lose_their_metadata_and_keep_their_size_and_orientation()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();

        var message = await UploadOkAsync(a, exchangeId, TestFiles.JpegWithExif(600, 400, orientation: 6), "photo.jpg");
        var (bytes, response) = await DownloadAsync(b, message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(TestFiles.Contains(bytes, TestFiles.Marker));
        Assert.False(TestFiles.Contains(bytes, "Exif"));
        using var picture = SKBitmap.Decode(bytes);
        Assert.Equal((400, 600), (picture.Width, picture.Height));   // повёрнута по EXIF (альбомная стала портретной), не уменьшена
        var topRight = picture.GetPixel(picture.Width - 10, 10);
        Assert.True(topRight.Red > 200 && topRight.Blue < 80, "красный угол должен оказаться справа сверху");
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_png_text_chunk_is_stripped_from_chat_images()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();

        var message = await UploadOkAsync(a, exchangeId, TestFiles.PngWithText(), "pic.png");

        var (bytes, _) = await DownloadAsync(b, message);
        Assert.False(TestFiles.Contains(bytes, TestFiles.Marker));
        Assert.False(TestFiles.Contains(bytes, "tEXt"));
    }

    [Fact]
    public async Task A_picture_with_too_many_pixels_is_refused_and_a_corrupt_one_too()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();

        var huge = await UploadAsync(a, exchangeId, TestFiles.Png(6000, 5000), "huge.png");
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
        Assert.Equal("image_too_large", await CodeAsync(huge));

        var broken = TestFiles.Png(300, 300);
        Array.Fill(broken, (byte)0x55, 60, broken.Length - 80);
        var corrupt = await UploadAsync(a, exchangeId, broken, "broken.png");
        Assert.Equal(HttpStatusCode.BadRequest, corrupt.StatusCode);
        Assert.Equal(0, await _factory.QueryAsync(db => db.Attachments.CountAsync(x => x.ExchangeId == exchangeId)));
    }

    // ---------- подписанные ссылки ----------

    [Fact]
    public async Task The_signed_link_lasts_five_minutes_and_forces_a_download_under_the_original_name()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var message = await UploadOkAsync(a, exchangeId, TestFiles.Pdf(), "Договор №1.pdf");
        var id = message.GetProperty("attachment").GetProperty("id").GetGuid();

        var link = await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/attachments/{id}/url"));
        var url = link.GetProperty("url").GetString()!;

        Assert.Contains("X-Amz-Signature=", url);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(url, "X-Amz-Expires=(29[5-9]|300)"), url);   // пять минут
        var expires = link.GetProperty("expiresAt").GetDateTime();
        Assert.InRange((expires - DateTime.UtcNow).TotalSeconds, 240, 305);
        Assert.Equal("Договор №1.pdf", link.GetProperty("fileName").GetString());

        var response = await _storageClient.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);                      // скачивание, а не показ в браузере
        Assert.Equal("Договор №1.pdf", disposition.FileNameStar);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(TestFiles.Pdf(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_tampered_or_unsigned_link_does_not_work_and_the_bucket_is_closed()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var message = await UploadOkAsync(a, exchangeId, TestFiles.Pdf(), "a.pdf");
        var id = message.GetProperty("attachment").GetProperty("id").GetGuid();
        var url = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/attachments/{id}/url"))).GetProperty("url").GetString()!;

        var withoutSignature = url[..url.IndexOf('?')];
        Assert.Equal(HttpStatusCode.Forbidden, (await _storageClient.GetAsync(withoutSignature)).StatusCode);       // бакет закрыт

        var tampered = url.Replace("X-Amz-Signature=", "X-Amz-Signature=00");
        Assert.NotEqual(HttpStatusCode.OK, (await _storageClient.GetAsync(tampered)).StatusCode);

        var otherObject = new Uri(url);
        var wrongKey = url.Replace(otherObject.AbsolutePath, otherObject.AbsolutePath + "x");
        Assert.NotEqual(HttpStatusCode.OK, (await _storageClient.GetAsync(wrongKey)).StatusCode);
    }

    [Fact]
    public async Task Only_participants_get_a_link_and_a_deleted_message_has_no_file()
    {
        var (world, a, b, exchangeId) = await ExchangeAsync();
        var stranger = await world.UserAsync(Beginner0, Beginner1);
        var message = await UploadOkAsync(a, exchangeId, TestFiles.Pdf(), "a.pdf");
        var id = message.GetProperty("attachment").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(a, HttpMethod.Get, $"/attachments/{id}/url")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(b, HttpMethod.Get, $"/attachments/{id}/url")).StatusCode);
        foreach (var who in new[] { stranger })
        {
            var refused = await SendAsync(who, HttpMethod.Get, $"/attachments/{id}/url");
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Equal("attachment_not_found", await CodeAsync(refused));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(a, HttpMethod.Get, $"/attachments/{Guid.NewGuid()}/url")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Get, $"/attachments/{id}/url")).StatusCode);

        // Автор удаляет сообщение: файл участникам больше не выдаётся, в истории вложения нет.
        await using var conn = await ChatAsync(a);
        await conn.InvokeAsync<JsonElement>("Delete", message.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(b, HttpMethod.Get, $"/attachments/{id}/url")).StatusCode);
        var item = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/exchanges/{exchangeId}/messages"))).GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("attachment").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("deletedAt").ValueKind);
    }

    [Fact]
    public async Task Thirty_days_after_the_exchange_closed_the_files_are_no_longer_available()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var message = await UploadOkAsync(a, exchangeId, TestFiles.Pdf(), "a.pdf");
        var id = message.GetProperty("attachment").GetProperty("id").GetGuid();
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == exchangeId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExchangeStatus.Completed).SetProperty(e => e.ClosedAt, DateTime.UtcNow.AddDays(-31)));
            return 0;
        });

        var response = await SendAsync(a, HttpMethod.Get, $"/attachments/{id}/url");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("chat_archived", await CodeAsync(response));
    }

    [Fact]
    public async Task In_a_read_only_chat_files_can_still_be_downloaded_but_not_uploaded()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var message = await UploadOkAsync(a, exchangeId, TestFiles.Pdf(), "a.pdf");
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExchangeStatus.Frozen));
            return 0;
        });

        var upload = await UploadAsync(b, exchangeId, TestFiles.Pdf(), "b.pdf");
        Assert.Equal(HttpStatusCode.Conflict, upload.StatusCode);
        Assert.Equal("chat_read_only", await CodeAsync(upload));
        Assert.Equal(HttpStatusCode.OK, (await DownloadAsync(b, message)).Response.StatusCode);
    }

    // ---------- доступ и сообщения ----------

    [Fact]
    public async Task A_stranger_cannot_upload_into_someone_elses_exchange_and_anonymous_calls_are_refused()
    {
        var (world, _, _, exchangeId) = await ExchangeAsync();
        var stranger = await world.UserAsync(Beginner0, Beginner1);

        var response = await UploadAsync(stranger, exchangeId, TestFiles.Pdf(), "a.pdf");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("exchange_not_found", await CodeAsync(response));
        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadAsync(null, exchangeId, TestFiles.Pdf(), "a.pdf")).StatusCode);
        Assert.Equal(0, await _factory.QueryAsync(db => db.Attachments.CountAsync(x => x.ExchangeId == exchangeId)));
    }

    [Fact]
    public async Task A_file_message_cannot_be_edited_but_can_be_deleted_within_15_minutes()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var message = await UploadOkAsync(a, exchangeId, TestFiles.Pdf(), "a.pdf");
        var id = message.GetProperty("id").GetGuid();
        await using var conn = await ChatAsync(a);

        var edit = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => conn.InvokeAsync<JsonElement>("Edit", id, "текст"));
        Assert.Contains("message_not_editable", edit.Message);

        var deleted = await conn.InvokeAsync<JsonElement>("Delete", id);
        Assert.Equal(id, deleted.GetProperty("messageId").GetGuid());
        Assert.Equal(0, await _factory.QueryAsync(db => db.MessageRevisions.CountAsync(r => r.MessageId == id)));   // у файла нет текста для записи о правке
    }

    private async Task<HubConnection> ChatAsync(MatchPerson who)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/hubs/chat"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(who.Token);
            })
            .Build();
        await connection.StartAsync();
        return connection;
    }

    // ---------- CORS и сбои хранилища ----------

    [Fact]
    public async Task The_bucket_allows_reading_from_the_frontend_origin_only()
    {
        var (_, a, b, exchangeId) = await ExchangeAsync();
        var message = await UploadOkAsync(a, exchangeId, TestFiles.Pdf(), "a.pdf");
        var id = message.GetProperty("attachment").GetProperty("id").GetGuid();
        var url = (await JsonAsync(await SendAsync(b, HttpMethod.Get, $"/attachments/{id}/url"))).GetProperty("url").GetString()!;

        var allowed = new HttpRequestMessage(HttpMethod.Get, url);
        allowed.Headers.Add("Origin", ApiFactory.FrontendUrl);
        var ok = await _storageClient.SendAsync(allowed);
        Assert.Equal(ApiFactory.FrontendUrl, ok.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var preflight = new HttpRequestMessage(HttpMethod.Options, url);
        preflight.Headers.Add("Origin", ApiFactory.FrontendUrl);
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        var answer = await _storageClient.SendAsync(preflight);
        Assert.True(answer.IsSuccessStatusCode, $"preflight: {(int)answer.StatusCode}");
        Assert.Equal(ApiFactory.FrontendUrl, answer.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var foreign = new HttpRequestMessage(HttpMethod.Get, url);
        foreign.Headers.Add("Origin", "https://evil.example");
        var denied = await _storageClient.SendAsync(foreign);
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));   // чужой сайт не получает разрешения читать
    }

    [Fact]
    public async Task When_the_storage_is_down_or_not_configured_the_answer_is_503_with_a_clear_code()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();

        using (var down = _factory.WithSettings("Testing", ("Storage:Endpoint", "http://127.0.0.1:1")))
        {
            var response = await Upload(down.CreateClient(), a, exchangeId);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("storage_unavailable", await CodeAsync(response));
        }

        using (var off = _factory.WithSettings("Testing", ("Storage:Endpoint", "")))
        {
            var response = await Upload(off.CreateClient(), a, exchangeId);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("storage_not_configured", await CodeAsync(response));
        }

        Assert.Equal(0, await _factory.QueryAsync(db => db.Attachments.CountAsync(x => x.ExchangeId == exchangeId)));   // записи без файла нет

        static async Task<HttpResponseMessage> Upload(HttpClient client, MatchPerson who, Guid exchangeId)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/exchanges/{exchangeId}/attachments");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
            var form = new MultipartFormDataContent { { new ByteArrayContent(TestFiles.Pdf()), "file", "a.pdf" } };
            request.Content = form;
            return await client.SendAsync(request);
        }
    }

    [Fact]
    public async Task A_request_far_above_the_limit_is_refused_by_the_server()
    {
        var (_, a, _, exchangeId) = await ExchangeAsync();
        var huge = new byte[25 * 1024 * 1024];
        Array.Fill(huge, (byte)'a');

        var response = await UploadAsync(a, exchangeId, huge, "huge.txt");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);   // не 500
    }
}
