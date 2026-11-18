using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Matching;

public enum MatchType
{
    /// <summary>Он преподаёт то, что я хочу изучать, и хочет изучать то, что преподаю я.</summary>
    Full,

    /// <summary>Он преподаёт то, что я хочу изучать, но не хочет ничего из моего: в заявке я предложу свой навык текстом (раздел 5.3 ТЗ).</summary>
    Partial,
}

/// <summary>Навык, по которому пара совпала, с уровнями обеих сторон.</summary>
/// <param name="TheirLevel">Уровень партнёра в этом навыке (в его «Умею» или «Хочу изучать»).</param>
/// <param name="MyLevel">Мой уровень в том же навыке (в моём «Хочу изучать» или «Умею»).</param>
public sealed record MatchedSkillDto(
    Guid SkillId,
    Guid CategoryId,
    string NameTg,
    string NameRu,
    string NameEn,
    SkillLevel TheirLevel,
    SkillLevel MyLevel);

/// <summary>Кандидат в выдаче подбора.</summary>
/// <param name="TimeZoneDifferenceHours">Разница часовых поясов с моим в часах (сейчас, с учётом перехода на летнее время).</param>
/// <param name="CommonLanguages">Языки занятий, общие со мной (хотя бы один: без общего языка человека в выдаче нет).</param>
/// <param name="TheyTeach">Что он преподаёт из моего «Хочу изучать» (уровень не ниже моего).</param>
/// <param name="TheyLearn">Что он хочет изучать из моего «Умею» (мой уровень не ниже его). Пусто у частичных матчей.</param>
public sealed record MatchDto(
    Guid UserId,
    string Name,
    string? AvatarUrl,
    string TimeZone,
    double TimeZoneDifferenceHours,
    string[] Languages,
    string[] CommonLanguages,
    bool IsMinor,
    decimal? RatingAvg,
    int RatingCount,
    bool IsNewcomer,
    MatchType MatchType,
    IReadOnlyList<MatchedSkillDto> TheyTeach,
    IReadOnlyList<MatchedSkillDto> TheyLearn);

/// <summary>Параметры запроса подбора, уже проверенные.</summary>
public sealed record MatchFilter(Guid? SkillId, string? Language, SkillLevel? Level, decimal? MinRating);

/// <summary>Навык с уровнем (мой «Умею» или «Хочу изучать»).</summary>
public sealed record LeveledSkill(Guid SkillId, SkillLevel Level);

/// <summary>Всё, что хранилищу нужно знать обо мне, чтобы найти кандидатов.</summary>
public sealed record MatchSeeker(
    Guid UserId,
    bool IsMinor,
    DateOnly AdultBirthCutoff,
    bool RestrictByAgeGroup,
    IReadOnlyList<LeveledSkill> Learn,
    IReadOnlyList<LeveledSkill> Teach);

/// <summary>Кандидат до сортировки и подробностей: только то, что нужно для порядка.</summary>
public sealed class MatchCandidate
{
    public Guid UserId { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public decimal RatingAvg { get; set; }
    public int RatingCount { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public bool IsFull { get; set; }

    /// <summary>Самый высокий уровень (1–3), на котором он преподаёт нужный мне навык.</summary>
    public int BestLevel { get; set; }

    /// <summary>Сколько навыков совпало в обе стороны вместе (те, что в <c>theyTeach</c> и <c>theyLearn</c>): при равенстве выше тот, у кого их больше.</summary>
    public int MatchedSkillCount { get; set; }
}

public sealed record MatchPageDetails(
    IReadOnlyDictionary<Guid, MatchUserInfo> Users,
    IReadOnlyDictionary<Guid, IReadOnlyList<MatchedSkillDto>> TheyTeach,
    IReadOnlyDictionary<Guid, IReadOnlyList<MatchedSkillDto>> TheyLearn,
    string[] SeekerLanguages);

public sealed record MatchUserInfo(
    Guid UserId, string Name, string? AvatarUrl, DateOnly BirthDate, string[] Languages);

public interface IMatchStore
{
    /// <summary>
    /// Все подходящие кандидаты (без сортировки и страниц). Кандидат: обычный активный видимый пользователь, не я, не связанный со мной
    /// блокировкой, той же возрастной группы (если включено), который преподаёт хотя бы один навык из моего «Хочу изучать»
    /// на уровне не ниже моего. Фильтры уже применены.
    /// </summary>
    Task<IReadOnlyList<MatchCandidate>> FindCandidatesAsync(MatchSeeker seeker, MatchFilter filter, CancellationToken ct);

    /// <summary>Подробности для одной страницы выдачи.</summary>
    Task<MatchPageDetails> LoadDetailsAsync(MatchSeeker seeker, IReadOnlyCollection<Guid> userIds, CancellationToken ct);
}
