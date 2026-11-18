using SkillSwap.Application.Common;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Profile;
using SkillSwap.Application.Settings;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Matching;

/// <param name="Skill">Показать только тех, кто преподаёт этот навык (он должен быть в моём «Хочу изучать»).</param>
/// <param name="Language">Код языка занятий ISO 639-1: у кандидата он должен быть в списке языков.</param>
/// <param name="Level">Уровень, на котором кандидат преподаёт нужный мне навык: Beginner, Intermediate или Advanced.</param>
/// <param name="MinRating">Минимальная средняя оценка (0–5). «Новички» без оценки при этом фильтре не показываются.</param>
public sealed record MatchQuery(Guid? Skill, string? Language, string? Level, decimal? MinRating, int Page, int PageSize);

/// <summary>Подбор пар (раздел 5.3 ТЗ): кто может научить меня тому, что я хочу изучать, и кому нужно то, что умею я.</summary>
public sealed class MatchService
{
    private readonly IProfileStore _profiles;
    private readonly IUserSkillStore _skills;
    private readonly IMatchStore _matches;
    private readonly ISystemSettingsStore _settings;
    private readonly TimeProvider _clock;

    public MatchService(
        IProfileStore profiles, IUserSkillStore skills, IMatchStore matches, ISystemSettingsStore settings, TimeProvider clock)
    {
        _profiles = profiles;
        _skills = skills;
        _matches = matches;
        _settings = settings;
        _clock = clock;
    }

    public async Task<PagedResult<MatchDto>> SearchAsync(Guid viewerId, bool viewerIsRegular, MatchQuery query, CancellationToken ct)
    {
        if (!viewerIsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Подбор доступен только обычным аккаунтам.");
        }

        Paging.Validate(query.Page, query.PageSize);
        var filter = ParseFilter(query);

        var viewer = await _profiles.FindUserAsync(viewerId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!viewer.IsRegular || viewer.Status != UserStatus.Active || viewer.DateOfBirth is null)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }

        // Подбор открывается после анкеты: минимум 1 навык в «Умею» и 1 в «Хочу изучать» (раздел 5 ТЗ).
        var mine = await _skills.ListActiveAsync(viewerId, ct);
        var learn = mine.Where(s => s.Direction == SkillDirection.Learn).Select(s => new LeveledSkill(s.SkillId, s.Level)).ToList();
        var teach = mine.Where(s => s.Direction == SkillDirection.Teach).Select(s => new LeveledSkill(s.SkillId, s.Level)).ToList();
        if (learn.Count == 0 || teach.Count == 0)
        {
            throw new ForbiddenException("skills_incomplete",
                "Подбор откроется после анкеты: добавьте минимум 1 навык в «Умею» и 1 в «Хочу изучать».");
        }

        var now = _clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var restrictByAge = await _settings.GetBoolAsync(SystemSettingKeys.MinorsMatchOnlyMinors, defaultValue: true, ct);
        var seeker = new MatchSeeker(
            viewerId, AgeGroups.IsMinor(viewer.DateOfBirth.Value, today), AgeGroups.AdultBirthCutoff(today), restrictByAge, learn, teach);

        var candidates = await _matches.FindCandidatesAsync(seeker, filter, ct);
        var ranked = MatchRanking.Order(candidates, viewer.TimeZone, now);

        var page = ranked.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList();
        if (page.Count == 0)
        {
            return new PagedResult<MatchDto>([], query.Page, query.PageSize, ranked.Count);
        }

        var details = await _matches.LoadDetailsAsync(seeker, page.Select(r => r.Candidate.UserId).ToList(), ct);
        var items = page.Select(r => ToDto(r, details, today)).ToList();
        return new PagedResult<MatchDto>(items, query.Page, query.PageSize, ranked.Count);
    }

    private static MatchFilter ParseFilter(MatchQuery query)
    {
        string? language = null;
        if (!string.IsNullOrWhiteSpace(query.Language))
        {
            language = LessonLanguages.Normalize(query.Language);
            if (!LessonLanguages.IsValid(language))
            {
                throw new BadRequestException("invalid_language", "Язык должен быть кодом ISO 639-1 из списка /languages.");
            }
        }

        SkillLevel? level = null;
        if (!string.IsNullOrWhiteSpace(query.Level))
        {
            var text = query.Level.Trim();
            if (int.TryParse(text, out _) || !Enum.TryParse<SkillLevel>(text, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                throw new BadRequestException("invalid_level", "Уровень: Beginner, Intermediate или Advanced.");
            }

            level = parsed;
        }

        if (query.MinRating is { } min && (min < 0m || min > 5m))
        {
            throw new BadRequestException("invalid_min_rating", "Минимальный рейтинг: число от 0 до 5.");
        }

        return new MatchFilter(query.Skill, language, level, query.MinRating is > 0m ? query.MinRating : null);
    }

    private static MatchDto ToDto(RankedCandidate ranked, MatchPageDetails details, DateOnly today)
    {
        var c = ranked.Candidate;
        var user = details.Users[c.UserId];
        var newcomer = c.RatingCount < User.NewcomerReviewThreshold;
        var common = user.Languages.Intersect(details.SeekerLanguages).Order(StringComparer.Ordinal).ToArray();

        return new MatchDto(
            c.UserId, user.Name, user.AvatarUrl, c.TimeZone,
            Math.Round(ranked.TimeZoneDifferenceMinutes / 60.0, 2),
            user.Languages, common, AgeGroups.IsMinor(user.BirthDate, today),
            newcomer ? null : c.RatingAvg, c.RatingCount, newcomer,
            c.IsFull ? MatchType.Full : MatchType.Partial,
            details.TheyTeach.TryGetValue(c.UserId, out var teach) ? teach : [],
            details.TheyLearn.TryGetValue(c.UserId, out var learn) ? learn : []);
    }
}
