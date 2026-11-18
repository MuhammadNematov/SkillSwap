using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Files;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Files;

/// <summary>Аватар (раздел 5 ТЗ): png, jpg, webp до 2 МБ; метаданные удаляются, картинка обрезается до квадрата и уменьшается до 512×512.</summary>
[Collection(ApiCollection.Name)]
public class AvatarTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public AvatarTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<MatchPerson> PersonAsync()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        return await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
    }

    private async Task<HttpResponseMessage> PutAvatarAsync(MatchPerson? who, byte[]? file, string fileName = "me.jpg")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/me/avatar");
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        }

        var form = new MultipartFormDataContent { { new StringContent("x"), "other" } };   // форма не пустая, но файла в ней нет
        if (file is not null)
        {
            var content = new ByteArrayContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");   // тип от клиента не доверяется
            form.Add(content, "file", fileName);
        }

        request.Content = form;
        return await _client.SendAsync(request);
    }

    private async Task<(byte[] Bytes, HttpResponseMessage Response)> GetAvatarAsync(Guid userId)
    {
        var response = await _client.GetAsync($"/api/v1/users/{userId}/avatar");   // без токена
        return (response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : [], response);
    }

    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("не картинка");

    private async Task<JsonElement> MeAsync(MatchPerson who)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        return await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
    }

    // ---------- загрузка и обработка ----------

    [Fact]
    public async Task A_large_photo_becomes_a_512_square_webp_without_metadata_and_is_rotated_by_its_exif()
    {
        var person = await PersonAsync();
        var original = TestFiles.JpegWithExif(1200, 800, orientation: 6);   // повернуть на 90° по часовой

        var response = await PutAvatarAsync(person, original);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("avatarUrl").GetString()!;
        Assert.StartsWith($"/api/v1/users/{person.Id}/avatar?v=", url);
        Assert.Equal(url, (await MeAsync(person)).GetProperty("avatarUrl").GetString());   // адрес лежит в профиле

        var (bytes, get) = await GetAvatarAsync(person.Id);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/webp", get.Content.Headers.ContentType!.MediaType);
        Assert.Contains("public", get.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());

        Assert.False(TestFiles.Contains(bytes, TestFiles.Marker));   // метаданных нет
        Assert.False(TestFiles.Contains(bytes, "Exif"));
        using var avatar = Decode(bytes);
        Assert.Equal((512, 512), (avatar.Width, avatar.Height));
        // Красный угол исходника (левый верхний) после поворота на 90° по часовой оказывается в правом верхнем углу.
        var topRight = avatar.GetPixel(avatar.Width - 20, 20);
        var topLeft = avatar.GetPixel(20, 20);
        Assert.True(topRight.Red > 200 && topRight.Blue < 80, $"правый верхний угол должен быть красным: {topRight}");
        Assert.True(topLeft.Blue > 200 && topLeft.Red < 80, $"левый верхний угол должен быть синим: {topLeft}");
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("webp")]
    public async Task Png_jpg_and_webp_are_accepted_and_all_become_webp(string format)
    {
        var person = await PersonAsync();
        var file = format switch { "png" => TestFiles.Png(600, 400), "jpg" => TestFiles.Jpeg(600, 400), _ => TestFiles.Webp(600, 400) };

        var response = await PutAvatarAsync(person, file, "a." + format);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var avatar = Decode((await GetAvatarAsync(person.Id)).Bytes);
        Assert.Equal((400, 400), (avatar.Width, avatar.Height));   // квадрат по меньшей стороне, не больше 512
    }

    [Fact]
    public async Task A_small_picture_is_not_enlarged()
    {
        var person = await PersonAsync();

        await PutAvatarAsync(person, TestFiles.Png(100, 100));

        using var avatar = Decode((await GetAvatarAsync(person.Id)).Bytes);
        Assert.Equal((100, 100), (avatar.Width, avatar.Height));
    }

    [Fact]
    public async Task A_png_with_a_text_chunk_loses_it()
    {
        var person = await PersonAsync();

        await PutAvatarAsync(person, TestFiles.PngWithText(), "a.png");

        Assert.False(TestFiles.Contains((await GetAvatarAsync(person.Id)).Bytes, TestFiles.Marker));
    }

    [Fact]
    public async Task The_stored_object_is_the_processed_one_in_a_closed_bucket()
    {
        var person = await PersonAsync();
        await PutAvatarAsync(person, TestFiles.JpegWithExif(900, 900, 1));

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IFileStorage>().GetAsync(AvatarService.KeyFor(person.Id), CancellationToken.None);

        Assert.NotNull(stored);
        await using var content = stored!.Content;
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory);
        Assert.False(TestFiles.Contains(memory.ToArray(), TestFiles.Marker));   // в хранилище уже очищенный файл, исходного там нет
        Assert.Equal("image/webp", stored.ContentType);
    }

    [Fact]
    public async Task Replacing_the_avatar_changes_the_version_and_the_picture()
    {
        var person = await PersonAsync();
        var first = (await (await PutAvatarAsync(person, TestFiles.Png(300, 300))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("avatarUrl").GetString();
        await Task.Delay(20);

        var second = (await (await PutAvatarAsync(person, TestFiles.Png(200, 200))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("avatarUrl").GetString();

        Assert.NotEqual(first, second);   // параметр v другой: кэш браузера сбрасывается
        using var avatar = Decode((await GetAvatarAsync(person.Id)).Bytes);
        Assert.Equal((200, 200), (avatar.Width, avatar.Height));
    }

    // ---------- отказы ----------

    [Fact]
    public async Task Other_formats_and_disguised_files_are_refused()
    {
        var person = await PersonAsync();
        using var bmp = TestFiles.Picture(32, 32);
        using var image = SKImage.FromBitmap(bmp);
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0, 0, 0, 0, 0x3B };

        foreach (var (file, name) in new (byte[], string)[]
        {
            (gif, "a.gif"),
            (TestFiles.Text("это не картинка вовсе, просто текст"), "a.png"),
            (TestFiles.Pdf(), "a.jpg"),
            ([], "empty.png"),
        })
        {
            var response = await PutAvatarAsync(person, file, name);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("unsupported_image", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await GetAvatarAsync(person.Id)).Response.StatusCode);   // ничего не сохранилось
    }

    [Fact]
    public async Task A_corrupt_image_with_a_valid_header_is_refused_not_a_500()
    {
        var person = await PersonAsync();
        var broken = TestFiles.Png(300, 300);
        Array.Fill(broken, (byte)0x55, 60, broken.Length - 80);   // заголовок цел, данные испорчены

        var response = await PutAvatarAsync(person, broken, "a.png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Over_two_megabytes_is_refused_with_413()
    {
        var person = await PersonAsync();
        var big = new byte[2 * 1024 * 1024 + 1];
        TestFiles.Png().CopyTo(big, 0);

        var response = await PutAvatarAsync(person, big, "big.png");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("avatar_too_large", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_picture_with_too_many_pixels_is_refused_without_decoding_it()
    {
        var person = await PersonAsync();

        var response = await PutAvatarAsync(person, TestFiles.Png(6000, 5000), "huge.png");   // 30 мегапикселей

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("image_too_large", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_request_without_a_file_or_a_login_is_refused()
    {
        var person = await PersonAsync();

        var noFile = await PutAvatarAsync(person, null);
        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);
        Assert.Equal("file_required", (await noFile.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAvatarAsync(null, TestFiles.Png())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.DeleteAsync("/api/v1/me/avatar")).StatusCode);
    }

    // ---------- удаление и видимость ----------

    [Fact]
    public async Task Removing_clears_the_profile_and_the_stored_object_and_repeats_are_harmless()
    {
        var person = await PersonAsync();
        await PutAvatarAsync(person, TestFiles.Png());

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me/avatar");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", person.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(delete)).StatusCode);

        Assert.Equal(JsonValueKind.Null, (await MeAsync(person)).GetProperty("avatarUrl").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAvatarAsync(person.Id)).Response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IFileStorage>().GetAsync(AvatarService.KeyFor(person.Id), CancellationToken.None));

        var again = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me/avatar");
        again.Headers.Authorization = new AuthenticationHeaderValue("Bearer", person.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(again)).StatusCode);   // и без аватара не ошибка
    }

    [Fact]
    public async Task The_avatar_of_a_blocked_or_unknown_user_is_not_served()
    {
        var person = await PersonAsync();
        await PutAvatarAsync(person, TestFiles.Png());
        Assert.Equal(HttpStatusCode.OK, (await GetAvatarAsync(person.Id)).Response.StatusCode);

        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == person.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Blocked));
            return 0;
        });

        Assert.Equal(HttpStatusCode.NotFound, (await GetAvatarAsync(person.Id)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAvatarAsync(Guid.NewGuid())).Response.StatusCode);
    }

    [Fact]
    public async Task The_avatar_url_shows_up_in_the_public_profile()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var owner = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        var viewer = await world.UserAsync((1, SkillLevel.Beginner), (0, SkillLevel.Beginner));
        await PutAvatarAsync(owner, TestFiles.Png());

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/users/{owner.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer.Token);
        var profile = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.StartsWith($"/api/v1/users/{owner.Id}/avatar?v=", profile.GetProperty("avatarUrl").GetString());
    }
}
