using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Files;

/// <summary>
/// Аватар (раздел 5 ТЗ): png, jpg или webp до 2 МБ. Сервер раскодирует картинку и кодирует заново: метаданные (EXIF, GPS, XMP) пропадают,
/// изображение обрезается до квадрата и уменьшается до 512×512 (меньшие не увеличиваются), формат хранения WebP. В закрытом бакете аватар
/// лежит под новым ключом при каждой смене; наружу в <c>avatarUrl</c> отдаётся подписанная ссылка на час (см. <see cref="AvatarUrls"/>) и только
/// тому, кто вправе видеть профиль. Открытого адреса у аватара нет.
/// </summary>
public sealed class AvatarService
{
    public const long MaxBytes = 2L * 1024 * 1024;
    public const int Size = 512;

    private readonly IProfileStore _profiles;
    private readonly IFileStorage _storage;
    private readonly IImageProcessor _images;
    private readonly AvatarUrls _urls;

    public AvatarService(IProfileStore profiles, IFileStorage storage, IImageProcessor images, AvatarUrls urls)
    {
        _profiles = profiles;
        _storage = storage;
        _images = images;
        _urls = urls;
    }

    public static string NewKey(Guid userId) => $"avatars/{userId:N}/{Guid.NewGuid():N}.webp";

    public async Task<AvatarDto> SetAsync(Guid userId, UploadedFile file, CancellationToken ct)
    {
        var user = await RequireActiveRegularAsync(userId, ct);

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

        var key = NewKey(userId);
        await using (var stream = new MemoryStream(avatar.Bytes, writable: false))
        {
            await _storage.PutAsync(key, stream, avatar.ContentType, ct);
        }

        await _profiles.SetAvatarKeyAsync(userId, key, ct);
        await TryDeleteAsync(user.AvatarKey);   // прежняя картинка больше не нужна
        return new AvatarDto(_urls.Sign(key) ?? throw new ServiceUnavailableException("storage_unavailable", "Хранилище файлов временно недоступно."));
    }

    /// <summary>Убрать аватар. Если его не было, ничего не происходит.</summary>
    public async Task RemoveAsync(Guid userId, CancellationToken ct)
    {
        var user = await RequireActiveRegularAsync(userId, ct);
        await _profiles.SetAvatarKeyAsync(userId, null, ct);
        await TryDeleteAsync(user.AvatarKey);
    }

    private async Task TryDeleteAsync(string? key)
    {
        if (key is null)
        {
            return;
        }

        try
        {
            await _storage.DeleteAsync(key, CancellationToken.None);
        }
        catch (ServiceUnavailableException)
        {
            // Хранилище недоступно: ссылка в профиле уже убрана, объект останется до сверки бакета (docs/DEFERRED.md).
        }
    }

    private async Task<Domain.Entities.User> RequireActiveRegularAsync(Guid userId, CancellationToken ct)
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

        return user;
    }
}
