using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Blocks;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Matching;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Settings;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Files;

/// <summary>
/// Адрес аватара для клиента: подписанная ссылка на <b>1 час</b>, и только тому, кто вправе видеть этот профиль. Бакет закрыт, постоянного
/// открытого адреса у аватара нет. Не вправе: заблокировавшие друг друга и (пока включена настройка) люди из разных возрастных групп (16–17 и 18+);
/// им вместо ссылки <c>null</c>. Свой аватар виден всегда. Если хранилище не настроено или недоступно, профиль всё равно отдаётся (аватар null).
/// </summary>
public sealed class AvatarUrls
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private const string ContentType = "image/webp";

    private readonly IFileStorage _storage;
    private readonly IUserBlockStore _blocks;
    private readonly IProfileStore _profiles;
    private readonly ISystemSettingsStore _settings;
    private readonly TimeProvider _clock;

    public AvatarUrls(IFileStorage storage, IUserBlockStore blocks, IProfileStore profiles, ISystemSettingsStore settings, TimeProvider clock)
    {
        _storage = storage;
        _blocks = blocks;
        _profiles = profiles;
        _settings = settings;
        _clock = clock;
    }

    /// <summary>Ссылка без проверки прав: когда право уже проверено (профиль открыт зрителю, человек в его подборе) или аватар свой.</summary>
    public string? Sign(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        try
        {
            return _storage.CreateViewUrl(key, ContentType, Lifetime);
        }
        catch (ServiceUnavailableException)
        {
            return null;
        }
    }

    /// <summary>Ссылки на аватары владельцев с проверкой прав зрителя: одним набором запросов на любой список.</summary>
    public async Task<IReadOnlyDictionary<Guid, string?>> ForViewerAsync(
        Guid viewerId, IEnumerable<(Guid OwnerId, string? Key)> owners, CancellationToken ct)
    {
        var withAvatar = owners.Where(o => !string.IsNullOrEmpty(o.Key)).DistinctBy(o => o.OwnerId).ToList();
        var result = new Dictionary<Guid, string?>();
        if (withAvatar.Count == 0)
        {
            return result;
        }

        var others = withAvatar.Select(o => o.OwnerId).Where(id => id != viewerId).ToList();
        var blocked = others.Count == 0 ? new HashSet<Guid>() : (await _blocks.BlockedEitherWayAmongAsync(viewerId, others, ct)).ToHashSet();

        var restrictByAge = others.Count > 0 && await _settings.GetBoolAsync(SystemSettingKeys.MinorsMatchOnlyMinors, defaultValue: true, ct);
        IReadOnlyDictionary<Guid, DateOnly> birth = new Dictionary<Guid, DateOnly>();
        if (restrictByAge)
        {
            birth = await _profiles.GetBirthDatesAsync([.. others, viewerId], ct);
        }

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        foreach (var (ownerId, key) in withAvatar)
        {
            var allowed = ownerId == viewerId || (!blocked.Contains(ownerId) && (!restrictByAge || SameAgeGroup(birth, viewerId, ownerId, today)));
            result[ownerId] = allowed ? Sign(key) : null;
        }

        return result;
    }

    private static bool SameAgeGroup(IReadOnlyDictionary<Guid, DateOnly> birth, Guid viewerId, Guid ownerId, DateOnly today) =>
        birth.TryGetValue(viewerId, out var a) && birth.TryGetValue(ownerId, out var b) && AgeGroups.IsMinor(a, today) == AgeGroups.IsMinor(b, today);
}
