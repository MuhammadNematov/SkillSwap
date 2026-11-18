using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Files;

/// <summary>
/// Аватар (раздел 5 ТЗ): png, jpg или webp до 2 МБ. Сервер раскодирует картинку и кодирует заново: метаданные (EXIF, GPS, XMP) пропадают,
/// изображение обрезается до квадрата и уменьшается до 512×512 (меньшие не увеличиваются), формат хранения WebP. В <c>AvatarUrl</c>
/// лежит адрес <c>/api/v1/users/{id}/avatar?v=…</c>: его отдаёт приложение (бакет закрытый), параметр <c>v</c> меняется при смене аватара.
/// </summary>
public sealed class AvatarService
{
    public const long MaxBytes = 2L * 1024 * 1024;
    public const int Size = 512;

    private readonly IProfileStore _profiles;
    private readonly IFileStorage _storage;
    private readonly IImageProcessor _images;
    private readonly TimeProvider _clock;

    public AvatarService(IProfileStore profiles, IFileStorage storage, IImageProcessor images, TimeProvider clock)
    {
        _profiles = profiles;
        _storage = storage;
        _images = images;
        _clock = clock;
    }

    public static string KeyFor(Guid userId) => $"avatars/{userId:N}.webp";

    public async Task<AvatarDto> SetAsync(Guid userId, UploadedFile file, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);

        if (file.Length > MaxBytes)
        {
            throw new PayloadTooLargeException("avatar_too_large", "Аватар не больше 2 МБ.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await file.Content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                throw new PayloadTooLargeException("avatar_too_large", "Аватар не больше 2 МБ.");
            }
        }

        var data = buffer.ToArray();
        var probe = data.Length == 0 ? null : _images.Probe(data);
        if (probe is null)
        {
            throw new BadRequestException("unsupported_image", "Аватар: png, jpg или webp.");
        }

        if ((long)probe.Value.Width * probe.Value.Height > FileInspector.MaxImagePixels)
        {
            throw new BadRequestException("image_too_large", "Картинка слишком большая по размерам (не больше 25 мегапикселей).");
        }

        ProcessedImage avatar;
        try
        {
            avatar = _images.MakeAvatar(data, Size);
        }
        catch (InvalidDataException)
        {
            throw new BadRequestException("unsupported_image", "Не удалось прочитать картинку: файл повреждён.");
        }

        await using (var stream = new MemoryStream(avatar.Bytes, writable: false))
        {
            await _storage.PutAsync(KeyFor(userId), stream, avatar.ContentType, ct);
        }

        var url = $"/api/v1/users/{userId}/avatar?v={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        await _profiles.SetAvatarUrlAsync(userId, url, ct);
        return new AvatarDto(url);
    }

    /// <summary>Убрать аватар. Если его не было, ничего не происходит.</summary>
    public async Task RemoveAsync(Guid userId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(userId, ct);
        await _profiles.SetAvatarUrlAsync(userId, null, ct);
        await _storage.DeleteAsync(KeyFor(userId), ct);
    }

    /// <summary>Картинка аватара для показа. Нет аватара, нет пользователя, служебный или удалённый аккаунт — 404.</summary>
    public async Task<StoredFile> GetAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct);
        if (user is null || !user.IsRegular || user.Status != UserStatus.Active || user.AvatarUrl is null)
        {
            throw new NotFoundException("avatar_not_found", "Аватар не найден.");
        }

        return await _storage.GetAsync(KeyFor(userId), ct) ?? throw new NotFoundException("avatar_not_found", "Аватар не найден.");
    }

    private async Task RequireActiveRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Аватар доступен только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }
    }
}
