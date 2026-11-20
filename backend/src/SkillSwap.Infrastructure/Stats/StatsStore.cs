using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Stats;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Stats;

/// <summary>Агрегаты для статистики: только счётчики и времена, без выбора персональных полей.</summary>
public sealed class StatsStore : IStatsStore
{
    private const string TakenAction = "CaseTaken";
    private const string AppealedAction = "CaseAppealed";
    private const string ConcludedAction = "AppealConcluded";

    private readonly AppDbContext _db;

    public StatsStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<UserStatsDto> UsersAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var regular = _db.Users.AsNoTracking().Where(u => u.AccountType == AccountType.Regular);
        var byStatus = await regular.GroupBy(u => u.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var staff = await _db.Users.AsNoTracking().Where(u => u.AccountType == AccountType.Staff && u.Status == UserStatus.Active)
            .GroupBy(u => u.Role).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return new UserStatsDto(
            byStatus.Where(x => x.Key == UserStatus.Active).Sum(x => x.Count),
            byStatus.Where(x => x.Key == UserStatus.Blocked).Sum(x => x.Count),
            byStatus.Where(x => x.Key == UserStatus.Deleted).Sum(x => x.Count),
            await regular.CountAsync(u => u.CreatedAt >= from && u.CreatedAt < to, ct),
            await regular.CountAsync(u => u.LastActiveAt != null && u.LastActiveAt >= from && u.LastActiveAt < to, ct),
            staff.Where(x => x.Key == UserRole.Moderator).Sum(x => x.Count),
            staff.Where(x => x.Key == UserRole.Admin).Sum(x => x.Count),
            staff.Where(x => x.Key == UserRole.SuperAdmin).Sum(x => x.Count));
    }

    public async Task<ExchangeStatsDto> ExchangesAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var exchanges = _db.Exchanges.AsNoTracking();
        var now = await exchanges.GroupBy(e => e.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var closed = exchanges.Where(e => e.ClosedAt != null && e.ClosedAt >= from && e.ClosedAt < to);
        return new ExchangeStatsDto(
            now.Where(x => x.Key == ExchangeStatus.Active).Sum(x => x.Count),
            now.Where(x => x.Key == ExchangeStatus.Frozen).Sum(x => x.Count),
            await exchanges.CountAsync(e => e.CreatedAt >= from && e.CreatedAt < to, ct),
            await closed.CountAsync(e => e.Status == ExchangeStatus.Completed, ct),
            await closed.CountAsync(e => e.Status == ExchangeStatus.Aborted, ct),
            await closed.CountAsync(e => e.Status == ExchangeStatus.Aborted && e.AbortedForNoShow, ct));
    }

    public async Task<LessonStatsDto> LessonsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var counts = await _db.Sessions.AsNoTracking().Where(s => s.StartAt >= from && s.StartAt < to)
            .GroupBy(s => s.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(params SessionStatus[] statuses) => counts.Where(x => statuses.Contains(x.Key)).Sum(x => x.Count);
        return new LessonStatsDto(
            Of(SessionStatus.Completed), Of(SessionStatus.NoShow), Of(SessionStatus.Missed), Of(SessionStatus.Cancelled, SessionStatus.LateCancelled),
            Of(SessionStatus.Unconfirmed, SessionStatus.NotCounted));
    }

    public async Task<CaseStatsDto> CasesAsync(DateTime from, DateTime to, DateTime now, CancellationToken ct)
    {
        var cases = _db.Cases.AsNoTracking();
        var opened = await cases.Where(c => c.CreatedAt >= from && c.CreatedAt < to).GroupBy(c => c.Type).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var resolved = await cases.Where(c => c.ResolvedAt != null && c.ResolvedAt >= from && c.ResolvedAt < to && c.Decision != null)
            .GroupBy(c => c.Decision!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var backlog = await cases.GroupBy(c => c.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var appealed = await _db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AppealedAction && a.TargetType == "Case" && a.CreatedAt >= from && a.CreatedAt < to, ct);
        return new CaseStatsDto(
            opened.Sum(x => x.Count),
            Fill<CaseType>(opened.ToDictionary(x => x.Key.ToString(), x => x.Count)),
            resolved.Sum(x => x.Count),
            Fill<CaseDecision>(resolved.ToDictionary(x => x.Key.ToString(), x => x.Count)),
            await cases.CountAsync(c => c.ClosedAt != null && c.ClosedAt >= from && c.ClosedAt < to, ct),
            appealed,
            Fill<CaseStatus>(backlog.ToDictionary(x => x.Key.ToString(), x => x.Count)),
            await cases.CountAsync(c => c.Status == CaseStatus.Open && c.DueAt < now, ct),
            await cases.CountAsync(c => c.OverdueMarkedAt != null && c.OverdueMarkedAt >= from && c.OverdueMarkedAt < to, ct),
            new ReactionStatsDto(0, null, null, null));
    }

    public async Task<IReadOnlyList<ReactionSample>> ReactionSamplesAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var (since, until) = (from, to);   // «from» внутри запроса в синтаксисе LINQ читается как начало выражения
        var firstTakes = _db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == TakenAction && a.TargetType == "Case" && a.TargetId != null)
            .GroupBy(a => a.TargetId!.Value).Select(g => new { CaseId = g.Key, At = g.Min(a => a.CreatedAt) });
        var rows = await (from t in firstTakes
                          join c in _db.Cases.AsNoTracking() on t.CaseId equals c.Id
                          where c.CreatedAt >= since && c.CreatedAt < until
                          select new { c.CreatedAt, t.At, c.DueAt }).ToListAsync(ct);
        return rows.Select(r => new ReactionSample((r.At - r.CreatedAt).TotalSeconds, r.At <= r.DueAt)).ToList();
    }

    public async Task<ModeratorCounts> ModeratorCountsAsync(Guid moderatorId, DateTime from, DateTime to, CancellationToken ct)
    {
        var audit = _db.AuditLogs.AsNoTracking().Where(a => a.ActorId == moderatorId && a.CreatedAt >= from && a.CreatedAt < to);
        var resolved = await _db.Cases.AsNoTracking()
            .Where(c => c.ModeratorId == moderatorId && c.ResolvedAt != null && c.ResolvedAt >= from && c.ResolvedAt < to && c.Decision != null)
            .GroupBy(c => c.Decision!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return new ModeratorCounts(
            await audit.CountAsync(a => a.Action == TakenAction, ct),
            resolved.Sum(x => x.Count),
            Fill<CaseDecision>(resolved.ToDictionary(x => x.Key.ToString(), x => x.Count)),
            await audit.CountAsync(a => a.Action == ConcludedAction, ct),
            await _db.Cases.AsNoTracking().CountAsync(c => c.ModeratorId == moderatorId && (c.Status == CaseStatus.InProgress || c.Status == CaseStatus.WaitingResponse), ct));
    }

    public async Task<IReadOnlyList<ReactionSample>> ModeratorReactionSamplesAsync(Guid moderatorId, DateTime from, DateTime to, CancellationToken ct)
    {
        // Его взятия за период по обращениям, у которых это взятие было первым за всё время (повторное взятие после возврата в очередь реакцией не считается).
        var mine = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == TakenAction && a.ActorId == moderatorId && a.TargetType == "Case" && a.TargetId != null && a.CreatedAt >= from && a.CreatedAt < to)
            .GroupBy(a => a.TargetId!.Value).Select(g => new { CaseId = g.Key, At = g.Min(a => a.CreatedAt) }).ToListAsync(ct);
        if (mine.Count == 0)
        {
            return [];
        }

        var ids = mine.Select(m => m.CaseId).ToList();
        var earliest = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == TakenAction && a.TargetType == "Case" && a.TargetId != null && ids.Contains(a.TargetId.Value))
            .GroupBy(a => a.TargetId!.Value).Select(g => new { CaseId = g.Key, At = g.Min(a => a.CreatedAt) }).ToDictionaryAsync(x => x.CaseId, x => x.At, ct);
        var first = mine.Where(m => earliest.TryGetValue(m.CaseId, out var at) && at == m.At).ToList();
        var firstIds = first.Select(m => m.CaseId).ToList();
        var cases = await _db.Cases.AsNoTracking().Where(c => firstIds.Contains(c.Id)).Select(c => new { c.Id, c.CreatedAt, c.DueAt }).ToDictionaryAsync(c => c.Id, ct);
        return first.Where(m => cases.ContainsKey(m.CaseId)).Select(m => new ReactionSample((m.At - cases[m.CaseId].CreatedAt).TotalSeconds, m.At <= cases[m.CaseId].DueAt)).ToList();
    }

    /// <summary>Словарь по всем значениям перечисления: отсутствующие получают 0, форма ответа не зависит от данных.</summary>
    private static IReadOnlyDictionary<string, int> Fill<TEnum>(Dictionary<string, int> counts) where TEnum : struct, Enum =>
        Enum.GetNames<TEnum>().ToDictionary(n => n, n => counts.GetValueOrDefault(n));
}
