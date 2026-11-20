using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Audit;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Audit;

public sealed class AuditJournalStore : IAuditJournalStore
{
    private readonly AppDbContext _db;

    public AuditJournalStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(IReadOnlyList<AuditLog> Items, int Total)> SearchAsync(Guid? limitedTo, AuditQuery q, CancellationToken ct)
    {
        var query = Scoped(limitedTo);
        if (q.ActorId is { } actor)
        {
            query = query.Where(a => a.ActorId == actor);
        }

        if (q.Action is not null)
        {
            query = query.Where(a => a.Action == q.Action);
        }

        if (q.TargetType is not null)
        {
            query = query.Where(a => a.TargetType == q.TargetType);
        }

        if (q.TargetId is { } target)
        {
            query = query.Where(a => a.TargetId == target);
        }

        if (q.From is { } from)
        {
            query = query.Where(a => a.CreatedAt >= from);
        }

        if (q.To is { } to)
        {
            query = query.Where(a => a.CreatedAt < to);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id).Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<IReadOnlyList<string>> ListActionsAsync(Guid? limitedTo, CancellationToken ct) =>
        await Scoped(limitedTo).Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync(ct);

    /// <summary>Админ видит записи модераторов (по текущей роли исполнителя) и свои; суперадмин всё, включая действия системы без исполнителя.</summary>
    private IQueryable<AuditLog> Scoped(Guid? limitedTo)
    {
        var all = _db.AuditLogs.AsNoTracking();
        return limitedTo is { } me
            ? all.Where(a => a.ActorId == me || _db.Users.Any(u => u.Id == a.ActorId && u.Role == UserRole.Moderator))
            : all;
    }
}
