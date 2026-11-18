using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SkillSwap.Application.Matching;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Matching;

public sealed class MatchStore : IMatchStore
{
    // Числовой уровень навыка в анкете: Beginner = 1, Intermediate = 2, Advanced = 3 (как в SkillLevel).
    private const string LevelCase = "CASE us.\"Level\" WHEN 'Beginner' THEN 1 WHEN 'Intermediate' THEN 2 ELSE 3 END";

    // Один запрос на всех кандидатов. Идея:
    //   cand        — кто вообще может попасть в выдачу (видимый, активный, не заблокирован, той же возрастной группы, с общим языком, фильтры);
    //   wanted      — включённые навыки, которые есть в моей анкете (остальные навыки кандидатов не интересуют);
    //   sk          — эти навыки у кандидатов с числовым уровнем;
    //   they_teach  — что кандидат преподаёт из моего «Хочу изучать» на уровне НЕ НИЖЕ моего (лучший уровень на человека);
    //   they_learn  — кто хочет изучать из моего «Умею», если мой уровень НЕ НИЖЕ его (зеркальное правило).
    // В выдаче остаются те, у кого есть they_teach; есть ещё и they_learn — полный матч.
    // Всё собирается соединениями и агрегатами, а не подзапросами «для каждой строки»: время растёт линейно, а не квадратично.
    private const string CandidatesSql = $$"""
        WITH me_learn AS (
            SELECT t.skill_id, t.level FROM unnest(@learnIds, @learnLevels) AS t(skill_id, level)
        ),
        me_teach AS (
            SELECT t.skill_id, t.level FROM unnest(@teachIds, @teachLevels) AS t(skill_id, level)
        ),
        my_langs AS (
            SELECT "LanguageCode" FROM "UserLanguages" WHERE "UserId" = @me
        ),
        wanted AS (
            SELECT s."Id"
            FROM "Skills" s
            JOIN "SkillCategories" sc ON sc."Id" = s."CategoryId" AND sc."IsActive"
            WHERE s."IsActive"
              AND (s."Id" IN (SELECT skill_id FROM me_learn) OR s."Id" IN (SELECT skill_id FROM me_teach))
        ),
        cand AS (
            SELECT u."Id", u."TimeZone", u."RatingAvg", u."RatingCount", u."LastActiveAt"
            FROM "Users" u
            WHERE u."AccountType" = 'Regular'
              AND u."Status" = 'Active'
              AND u."IsSearchable"
              AND u."Id" <> @me
              AND (NOT @restrictByAge OR (u."DateOfBirth" IS NOT NULL AND (u."DateOfBirth" > @cutoff) = @meIsMinor))
              AND NOT EXISTS (
                  SELECT 1 FROM "UserBlocks" b
                  WHERE (b."UserId" = @me AND b."BlockedUserId" = u."Id")
                     OR (b."UserId" = u."Id" AND b."BlockedUserId" = @me))
              AND (@minRating IS NULL OR (u."RatingCount" >= @newcomerThreshold AND u."RatingAvg" >= @minRating))
              -- урок без общего языка невозможен: общий язык занятий со мной обязателен (не критерий порядка, а условие отбора)
              AND EXISTS (
                  SELECT 1 FROM "UserLanguages" cl
                  WHERE cl."UserId" = u."Id" AND cl."LanguageCode" IN (SELECT "LanguageCode" FROM my_langs))
              AND (@lang IS NULL OR EXISTS (
                  SELECT 1 FROM "UserLanguages" l WHERE l."UserId" = u."Id" AND l."LanguageCode" = @lang))
        ),
        sk AS (
            SELECT us."UserId" AS user_id, us."SkillId" AS skill_id, us."Direction" AS direction, {{LevelCase}} AS lvl
            FROM "UserSkills" us
            JOIN wanted w ON w."Id" = us."SkillId"
            JOIN cand c ON c."Id" = us."UserId"
        ),
        they_teach AS (
            -- n и best считаются по всем подходящим парам; фильтры skill и level только решают, остаётся ли человек в выдаче
            SELECT sk.user_id,
                   max(sk.lvl) AS best,
                   count(*) AS n,
                   bool_or((@skill IS NULL OR sk.skill_id = @skill) AND (@level IS NULL OR sk.lvl = @level)) AS passes
            FROM sk JOIN me_learn ml ON ml.skill_id = sk.skill_id
            WHERE sk.direction = 'Teach' AND sk.lvl >= ml.level
            GROUP BY sk.user_id
        ),
        they_learn AS (
            SELECT sk.user_id, count(*) AS n
            FROM sk JOIN me_teach mt ON mt.skill_id = sk.skill_id
            WHERE sk.direction = 'Learn' AND mt.level >= sk.lvl
            GROUP BY sk.user_id
        )
        SELECT c."Id" AS "UserId", c."TimeZone", c."RatingAvg", c."RatingCount", c."LastActiveAt",
               (tl.user_id IS NOT NULL) AS "IsFull",
               tt.best AS "BestLevel",
               (tt.n + COALESCE(tl.n, 0))::int AS "MatchedSkillCount"
        FROM cand c
        JOIN they_teach tt ON tt.user_id = c."Id" AND tt.passes
        LEFT JOIN they_learn tl ON tl.user_id = c."Id"
        """;

    private readonly AppDbContext _db;

    public MatchStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<MatchCandidate>> FindCandidatesAsync(MatchSeeker seeker, MatchFilter filter, CancellationToken ct) =>
        await _db.Database.SqlQueryRaw<MatchCandidate>(CandidatesSql, Parameters(seeker, filter)).ToListAsync(ct);

    // Параметры запроса кандидатов.
    private static NpgsqlParameter[] Parameters(MatchSeeker seeker, MatchFilter filter)
    {
        return new[]
        {
            new NpgsqlParameter("me", NpgsqlDbType.Uuid) { Value = seeker.UserId },
            new NpgsqlParameter("restrictByAge", NpgsqlDbType.Boolean) { Value = seeker.RestrictByAgeGroup },
            new NpgsqlParameter("cutoff", NpgsqlDbType.Date) { Value = seeker.AdultBirthCutoff },
            new NpgsqlParameter("meIsMinor", NpgsqlDbType.Boolean) { Value = seeker.IsMinor },
            new NpgsqlParameter("minRating", NpgsqlDbType.Numeric) { Value = (object?)filter.MinRating ?? DBNull.Value },
            new NpgsqlParameter("newcomerThreshold", NpgsqlDbType.Integer) { Value = Domain.Entities.User.NewcomerReviewThreshold },
            new NpgsqlParameter("lang", NpgsqlDbType.Text) { Value = (object?)filter.Language ?? DBNull.Value },
            new NpgsqlParameter("skill", NpgsqlDbType.Uuid) { Value = (object?)filter.SkillId ?? DBNull.Value },
            new NpgsqlParameter("level", NpgsqlDbType.Integer) { Value = filter.Level is { } l ? (int)l : DBNull.Value },
            new NpgsqlParameter("learnIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seeker.Learn.Select(s => s.SkillId).ToArray() },
            new NpgsqlParameter("learnLevels", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = seeker.Learn.Select(s => (int)s.Level).ToArray() },
            new NpgsqlParameter("teachIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = seeker.Teach.Select(s => s.SkillId).ToArray() },
            new NpgsqlParameter("teachLevels", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = seeker.Teach.Select(s => (int)s.Level).ToArray() },
        };
    }

    public async Task<MatchPageDetails> LoadDetailsAsync(MatchSeeker seeker, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var users = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Name, u.AvatarUrl, u.DateOfBirth })
            .ToListAsync(ct);

        var languageRows = await _db.UserLanguages.AsNoTracking()
            .Where(l => userIds.Contains(l.UserId) || l.UserId == seeker.UserId)
            .Select(l => new { l.UserId, l.LanguageCode })
            .ToListAsync(ct);

        var learnIds = seeker.Learn.Select(s => s.SkillId).ToList();
        var teachIds = seeker.Teach.Select(s => s.SkillId).ToList();
        var skillRows = await _db.UserSkills.AsNoTracking()
            .Where(s => userIds.Contains(s.UserId)
                && s.Skill!.IsActive && s.Skill.Category!.IsActive
                && ((s.Direction == SkillDirection.Teach && learnIds.Contains(s.SkillId))
                    || (s.Direction == SkillDirection.Learn && teachIds.Contains(s.SkillId))))
            .Select(s => new
            {
                s.UserId, s.Direction, s.SkillId, s.Skill!.CategoryId, s.Skill.NameTg, s.Skill.NameRu, s.Skill.NameEn, s.Level,
            })
            .ToListAsync(ct);

        var myLearn = seeker.Learn.ToDictionary(s => s.SkillId, s => s.Level);
        var myTeach = seeker.Teach.ToDictionary(s => s.SkillId, s => s.Level);

        // Те же правила уровней, что и в SQL: преподаёт он — его уровень не ниже моего; преподаю я — мой не ниже его.
        var theyTeach = skillRows
            .Where(r => r.Direction == SkillDirection.Teach && r.Level >= myLearn[r.SkillId])
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MatchedSkillDto>)g
                .OrderBy(r => r.NameEn).ThenBy(r => r.SkillId)
                .Select(r => new MatchedSkillDto(r.SkillId, r.CategoryId, r.NameTg, r.NameRu, r.NameEn, r.Level, myLearn[r.SkillId]))
                .ToList());
        var theyLearn = skillRows
            .Where(r => r.Direction == SkillDirection.Learn && myTeach[r.SkillId] >= r.Level)
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MatchedSkillDto>)g
                .OrderBy(r => r.NameEn).ThenBy(r => r.SkillId)
                .Select(r => new MatchedSkillDto(r.SkillId, r.CategoryId, r.NameTg, r.NameRu, r.NameEn, r.Level, myTeach[r.SkillId]))
                .ToList());

        var languages = languageRows.GroupBy(l => l.UserId).ToDictionary(g => g.Key, g => g.Select(l => l.LanguageCode).Order(StringComparer.Ordinal).ToArray());

        return new MatchPageDetails(
            users.ToDictionary(
                u => u.Id,
                u => new MatchUserInfo(u.Id, u.Name, u.AvatarUrl, u.DateOfBirth ?? default, languages.GetValueOrDefault(u.Id, []))),
            theyTeach,
            theyLearn,
            languages.GetValueOrDefault(seeker.UserId, []));
    }
}
