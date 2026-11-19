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

/// <summary>
/// Аватар (раздел 5 ТЗ): png, jpg, webp до 2 МБ; метаданные удаляются, картинка обрезается до квадрата и уменьшается до 512×512.
/// Бакет закрыт: аватар отдаётся в <c>avatarUrl</c> подписанной ссылкой на час и только тому, кто вправе видеть профиль.
/// </summary>
[Collection(ApiCollection.Name)]
public class AvatarTests
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;
    private readonly HttpClient _storageClient = new();

    public AvatarTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<MatchPerson> PersonAsync(DateOnly? birth = null)
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        return await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner), new MatchTestWorld.Options(BirthDate: birth));
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

    private async Task<HttpResponseMessage> GetAsync(MatchPerson? who, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        }

        return await _client.SendAsync(request);
    }

    private async Task<string?> AvatarUrlInAsync(MatchPerson viewer, string url)
    {
        var json = await (await GetAsync(viewer, url)).Content.ReadFromJsonAsync<JsonElement>();
        var value = json.GetProperty("avatarUrl");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    /// <summary>Свой аватар по ссылке из профиля: байты и ответ хранилища.</summary>
    private async Task<(byte[] Bytes, HttpResponseMessage Response)> DownloadOwnAsync(MatchPerson person)
    {
        var url = await AvatarUrlInAsync(person, "/api/v1/me") ?? throw new InvalidOperationException("в профиле нет аватара");
        var response = await _storageClient.GetAsync(url);
        return (response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : [], response);
    }

    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("не картинка");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    // ---------- загрузка и обработка ----------

    [Fact]
    public async Task A_large_photo_becomes_a_512_square_webp_without_metadata_and_is_rotated_by_its_exif()
    {
        var person = await PersonAsync();
        var original = TestFiles.JpegWithExif(1200, 800, orientation: 6);   // повернуть на 90° по часовой

        var response = await PutAvatarAsync(person, original);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = (await JsonAsync(response)).GetProperty("avatarUrl").GetString()!;
        Assert.Contains("X-Amz-Signature=", url);   // ответ загрузки уже содержит подписанную ссылку
        Assert.StartsWith("http", await AvatarUrlInAsync(person, "/api/v1/me"));   // в профиле ссылка, а не ключ

        var (bytes, get) = await DownloadOwnAsync(person);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/webp", get.Content.Headers.ContentType!.MediaType);
        Assert.Equal("inline", get.Content.Headers.ContentDisposition!.DispositionType);   // показывается в <img>, а не скачивается

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
        using var avatar = Decode((await DownloadOwnAsync(person)).Bytes);
        Assert.Equal((400, 400), (avatar.Width, avatar.Height));   // квадрат по меньшей стороне, не больше 512
    }

    [Fact]
    public async Task A_small_picture_is_not_enlarged()
    {
        var person = await PersonAsync();

        await PutAvatarAsync(person, TestFiles.Png(100, 100));

        using var avatar = Decode((await DownloadOwnAsync(person)).Bytes);
        Assert.Equal((100, 100), (avatar.Width, avatar.Height));
    }

    [Fact]
    public async Task A_png_with_a_text_chunk_loses_it()
    {
        var person = await PersonAsync();

        await PutAvatarAsync(person, TestFiles.PngWithText(), "a.png");

        Assert.False(TestFiles.Contains((await DownloadOwnAsync(person)).Bytes, TestFiles.Marker));
    }

    [Fact]
    public async Task Replacing_the_avatar_gives_a_new_link_and_deletes_the_old_object()
    {
        var person = await PersonAsync();
        await PutAvatarAsync(person, TestFiles.Png(300, 300));
        var oldKey = await _factory.QueryAsync(db => db.Users.Where(u => u.Id == person.Id).Select(u => u.AvatarKey).SingleAsync());
        var first = await AvatarUrlInAsync(person, "/api/v1/me");

        await PutAvatarAsync(person, TestFiles.Png(200, 200));

        var second = await AvatarUrlInAsync(person, "/api/v1/me");
        Assert.NotEqual(first, second);
        using var avatar = Decode((await DownloadOwnAsync(person)).Bytes);
        Assert.Equal((200, 200), (avatar.Width, avatar.Height));
        using var scope = _factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IFileStorage>().GetAsync(oldKey!, CancellationToken.None));   // старая картинка удалена
    }

    [Fact]
    public async Task The_database_holds_a_storage_key_never_a_url_and_the_key_is_not_leaked()
    {
        var person = await PersonAsync();
        await PutAvatarAsync(person, TestFiles.Png());

        var key = await _factory.QueryAsync(db => db.Users.Where(u => u.Id == person.Id).Select(u => u.AvatarKey).SingleAsync());
        var shown = await AvatarUrlInAsync(person, "/api/v1/me");

        Assert.StartsWith($"avatars/{person.Id:N}/", key);
        Assert.StartsWith("http", shown);
        Assert.DoesNotContain(key!.Split('/')[^1], shown!.Split('?')[0].Replace(key, ""));   // ключ встречается только внутри адреса объекта
    }

    // ---------- подписанная ссылка: срок и закрытый бакет ----------

    [Fact]
    public async Task The_link_lasts_one_hour_and_the_bucket_itself_stays_closed()
    {
        var person = await PersonAsync();
        await PutAvatarAsync(person, TestFiles.Png());
        var url = (await AvatarUrlInAsync(person, "/api/v1/me"))!;

        SignedLinks.AssertLifetime(url, TimeSpan.FromHours(1));   // час
        Assert.Equal(HttpStatusCode.OK, (await _storageClient.GetAsync(url)).StatusCode);

        var unsigned = url[..url.IndexOf('?')];
        Assert.Equal(HttpStatusCode.Forbidden, (await _storageClient.GetAsync(unsigned)).StatusCode);   // постоянного открытого адреса нет
    }

    [Fact]
    public async Task There_is_no_open_avatar_endpoint_any_more()
    {
        var person = await PersonAsync();
        await PutAvatarAsync(person, TestFiles.Png());

        var response = await _client.GetAsync($"/api/v1/users/{person.Id}/avatar");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_link_is_built_on_the_public_endpoint_when_it_differs_from_the_internal_one()
    {
        var person = await PersonAsync();
        await PutAvatarAsync(person, TestFiles.Png());
        using var copy = _factory.WithSettings("Testing", ("Storage:PublicEndpoint", "https://files.skillswap.test"));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", person.Token);
        var profile = await JsonAsync(await copy.CreateClient().SendAsync(request));

        Assert.StartsWith("https://files.skillswap.test/", profile.GetProperty("avatarUrl").GetString());
    }

    // ---------- кто видит аватар ----------

    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B)> PairWithAvatarsAsync()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var a = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        var b = await world.UserAsync((1, SkillLevel.Beginner), (0, SkillLevel.Beginner));
        await PutAvatarAsync(a, TestFiles.Png());
        await PutAvatarAsync(b, TestFiles.Png());
        return (world, a, b);
    }

    [Fact]
    public async Task The_avatar_appears_in_the_public_profile_and_in_matches_for_those_who_may_see_the_person()
    {
        var (_, a, b) = await PairWithAvatarsAsync();

        var inProfile = await AvatarUrlInAsync(b, $"/api/v1/users/{a.Id}");
        Assert.Contains("X-Amz-Signature=", inProfile);
        Assert.Equal(HttpStatusCode.OK, (await _storageClient.GetAsync(inProfile!)).StatusCode);

        var matches = await JsonAsync(await GetAsync(b, "/api/v1/matches"));
        var item = matches.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("userId").GetGuid() == a.Id);
        Assert.Contains("X-Amz-Signature=", item.GetProperty("avatarUrl").GetString());
    }

    [Fact]
    public async Task Requests_and_exchanges_show_the_counterpart_avatar_to_participants()
    {
        var (world, a, b) = await PairWithAvatarsAsync();
        var created = await _client.SendAsync(Post(a, "/api/v1/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 }));
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();

        var request = await JsonAsync(await GetAsync(b, $"/api/v1/exchange-requests/{requestId}"));
        Assert.Contains("X-Amz-Signature=", request.GetProperty("fromUser").GetProperty("avatarUrl").GetString());
        Assert.Contains("X-Amz-Signature=", request.GetProperty("toUser").GetProperty("avatarUrl").GetString());

        var accept = await _client.SendAsync(Post(b, $"/api/v1/exchange-requests/{requestId}/accept", null));
        var exchangeId = (await JsonAsync(accept)).GetProperty("exchangeId").GetGuid();
        var exchange = await JsonAsync(await GetAsync(a, $"/api/v1/exchanges/{exchangeId}"));
        Assert.Contains("X-Amz-Signature=", exchange.GetProperty("partner").GetProperty("avatarUrl").GetString());
        var list = await JsonAsync(await GetAsync(a, "/api/v1/exchanges"));
        Assert.Contains("X-Amz-Signature=", list.GetProperty("items")[0].GetProperty("partner").GetProperty("avatarUrl").GetString());
    }

    [Fact]
    public async Task After_a_block_neither_side_sees_the_others_avatar_anywhere()
    {
        var (world, a, b) = await PairWithAvatarsAsync();
        var created = await _client.SendAsync(Post(a, "/api/v1/exchange-requests",
            new { toUserId = b.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 }));
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var accept = await _client.SendAsync(Post(b, $"/api/v1/exchange-requests/{requestId}/accept", null));
        var exchangeId = (await JsonAsync(accept)).GetProperty("exchangeId").GetGuid();

        await _client.SendAsync(Post(a, $"/api/v1/users/{b.Id}/block", null));

        foreach (var (viewer, partner) in new[] { (a, b), (b, a) })
        {
            var exchange = await JsonAsync(await GetAsync(viewer, $"/api/v1/exchanges/{exchangeId}"));
            Assert.Equal(JsonValueKind.Null, exchange.GetProperty("partner").GetProperty("avatarUrl").ValueKind);   // обмен прерван, но аватар скрыт
            var request = await JsonAsync(await GetAsync(viewer, $"/api/v1/exchange-requests/{requestId}"));
            var other = request.GetProperty(request.GetProperty("fromUser").GetProperty("id").GetGuid() == partner.Id ? "fromUser" : "toUser");
            Assert.Equal(JsonValueKind.Null, other.GetProperty("avatarUrl").ValueKind);
            Assert.Equal("Aborted", exchange.GetProperty("status").GetString());
        }

        // Себя человек по-прежнему видит со своим аватаром.
        Assert.NotNull(await AvatarUrlInAsync(a, "/api/v1/me"));

        // Список заблокированных: профиль закрыт, поэтому и аватара нет.
        var blocked = await JsonAsync(await GetAsync(a, "/api/v1/me/blocks"));
        Assert.Equal(JsonValueKind.Null, blocked[0].GetProperty("avatarUrl").ValueKind);
    }

    [Fact]
    public async Task The_age_group_rule_hides_avatars_between_minors_and_adults_but_not_between_equals()
    {
        var adult1 = await PersonAsync(new DateOnly(1990, 1, 1));
        var adult2 = await PersonAsync(new DateOnly(1995, 1, 1));
        var minor = await PersonAsync(DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-17)));
        foreach (var person in new[] { adult1, adult2, minor })
        {
            await PutAvatarAsync(person, TestFiles.Png());
        }

        async Task<IReadOnlyDictionary<Guid, string?>> SeenBy(MatchPerson viewer, params MatchPerson[] owners)
        {
            using var scope = _factory.Services.CreateScope();
            var keys = await _factory.QueryAsync(db => db.Users.Where(u => owners.Select(o => o.Id).Contains(u.Id)).Select(u => new { u.Id, u.AvatarKey }).ToListAsync());
            return await scope.ServiceProvider.GetRequiredService<AvatarUrls>().ForViewerAsync(viewer.Id, keys.Select(k => (k.Id, k.AvatarKey)), CancellationToken.None);
        }

        var adultSees = await SeenBy(adult1, adult2, minor);
        Assert.NotNull(adultSees[adult2.Id]);
        Assert.Null(adultSees[minor.Id]);                                    // взрослый не видит аватар подростка
        Assert.Null((await SeenBy(minor, adult1))[adult1.Id]);               // и подросток — взрослого
        Assert.NotNull((await SeenBy(minor, minor))[minor.Id]);              // себя видит всегда

        await _factory.QueryAsync(async db =>
        {
            await db.SystemSettings.Where(s => s.Key == SystemSettingKeys.MinorsMatchOnlyMinors).ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, "false"));
            return 0;
        });
        try
        {
            Assert.NotNull((await SeenBy(adult1, minor))[minor.Id]);         // настройка выключена: ограничений нет
        }
        finally
        {
            await _factory.QueryAsync(async db =>
            {
                await db.SystemSettings.Where(s => s.Key == SystemSettingKeys.MinorsMatchOnlyMinors).ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, "true"));
                return 0;
            });
        }
    }

    [Fact]
    public async Task When_the_storage_is_down_profiles_are_still_served_without_an_avatar()
    {
        var person = await PersonAsync();
        await PutAvatarAsync(person, TestFiles.Png());
        using var off = _factory.WithSettings("Testing", ("Storage:Endpoint", ""));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", person.Token);
        var response = await off.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // не 503: профиль важнее картинки
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(response)).GetProperty("avatarUrl").ValueKind);
    }

    private static HttpRequestMessage Post(MatchPerson who, string url, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    // ---------- отказы ----------

    [Fact]
    public async Task Other_formats_and_disguised_files_are_refused()
    {
        var person = await PersonAsync();
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
            Assert.Equal("unsupported_image", (await JsonAsync(response)).GetProperty("code").GetString());
        }

        Assert.Null(await AvatarUrlInAsync(person, "/api/v1/me"));   // ничего не сохранилось
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
        Assert.Equal("avatar_too_large", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_picture_with_too_many_pixels_is_refused_without_decoding_it()
    {
        var person = await PersonAsync();

        var response = await PutAvatarAsync(person, TestFiles.Png(6000, 5000), "huge.png");   // 30 мегапикселей

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("image_too_large", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_request_without_a_file_or_a_login_is_refused()
    {
        var person = await PersonAsync();

        var noFile = await PutAvatarAsync(person, null);
        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);
        Assert.Equal("file_required", (await JsonAsync(noFile)).GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await PutAvatarAsync(null, TestFiles.Png())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.DeleteAsync("/api/v1/me/avatar")).StatusCode);
    }

    // ---------- удаление ----------

    [Fact]
    public async Task Removing_clears_the_profile_and_the_stored_object_and_repeats_are_harmless()
    {
        var person = await PersonAsync();
        await PutAvatarAsync(person, TestFiles.Png());
        var key = await _factory.QueryAsync(db => db.Users.Where(u => u.Id == person.Id).Select(u => u.AvatarKey).SingleAsync());

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me/avatar");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", person.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(delete)).StatusCode);

        Assert.Null(await AvatarUrlInAsync(person, "/api/v1/me"));
        using var scope = _factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IFileStorage>().GetAsync(key!, CancellationToken.None));

        var again = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/me/avatar");
        again.Headers.Authorization = new AuthenticationHeaderValue("Bearer", person.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(again)).StatusCode);   // и без аватара не ошибка
    }
}
