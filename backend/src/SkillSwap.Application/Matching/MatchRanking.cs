using SkillSwap.Application.Common;
using SkillSwap.Domain.Entities;

namespace SkillSwap.Application.Matching;

public sealed record RankedCandidate(MatchCandidate Candidate, int TimeZoneDifferenceMinutes);

/// <summary>
/// Порядок выдачи подбора (раздел 5.3 ТЗ). Ключи по убыванию важности:
/// <list type="number">
/// <item>полный матч выше частичного;</item>
/// <item>меньше разница часовых поясов;</item>
/// <item>выше уровень, на котором партнёр преподаёт нужный мне навык;</item>
/// <item>выше рейтинг (у «Новичка» оценки нет, он ниже оценённых);</item>
/// <item>был активен за последние 7 дней;</item>
/// <item>больше совпавших навыков (<c>theyTeach</c> + <c>theyLearn</c>);</item>
/// <item>позже был активен; затем по Id, чтобы порядок на страницах не «прыгал».</item>
/// </list>
/// Общий язык занятий здесь не критерий: без него человек в выдачу вообще не попадает (условие отбора в запросе).
/// Чистая функция без обращений к БД: порядок проверяется обычными тестами.
/// </summary>
public static class MatchRanking
{
    public static readonly TimeSpan ActivityWindow = TimeSpan.FromDays(7);

    public static List<RankedCandidate> Order(IEnumerable<MatchCandidate> candidates, string seekerTimeZone, DateTimeOffset now)
    {
        var mine = TimeZoneOffsets.Minutes(seekerTimeZone, now);
        var activeSince = now.UtcDateTime - ActivityWindow;

        return candidates
            .Select(c => new RankedCandidate(c, Math.Abs(TimeZoneOffsets.Minutes(c.TimeZone, now) - mine)))
            .OrderByDescending(r => r.Candidate.IsFull)
            .ThenBy(r => r.TimeZoneDifferenceMinutes)
            .ThenByDescending(r => r.Candidate.BestLevel)
            .ThenByDescending(r => r.Candidate.RatingCount >= User.NewcomerReviewThreshold ? r.Candidate.RatingAvg : -1m)
            .ThenByDescending(r => r.Candidate.LastActiveAt >= activeSince)
            .ThenByDescending(r => r.Candidate.MatchedSkillCount)
            .ThenByDescending(r => r.Candidate.LastActiveAt ?? DateTime.MinValue)
            .ThenBy(r => r.Candidate.UserId)
            .ToList();
    }
}
