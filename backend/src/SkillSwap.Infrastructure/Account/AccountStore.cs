using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Account;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Account;

public sealed class AccountStore : IAccountStore
{
    private readonly AppDbContext _db;

    public AccountStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task DeleteProfileDataAsync(Guid userId, CancellationToken ct)
    {
        await _db.UserSkills.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.UserLanguages.Where(l => l.UserId == userId).ExecuteDeleteAsync(ct);
    }

    public Task DeleteNotificationsAsync(Guid userId, CancellationToken ct) =>
        _db.Notifications.Where(n => n.UserId == userId).ExecuteDeleteAsync(ct);

    public async Task AnonymizeNotificationNamesAsync(Guid userId, string placeholder, CancellationToken ct)
    {
        var id = userId.ToString();
        var path = new[] { "userName" };
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE "Notifications"
            SET "PayloadJson" = jsonb_set("PayloadJson", {path}::text[], to_jsonb({placeholder}::text))
            WHERE "PayloadJson"->>'userId' = {id} AND "PayloadJson" ? 'userName'
            """, ct);
    }
}

public sealed class DataExportStore : IDataExportStore
{
    private readonly AppDbContext _db;

    public DataExportStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(DataExport export) => _db.DataExports.Add(export);

    public Task<DataExport?> FindAsync(Guid id, CancellationToken ct) => _db.DataExports.FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<DataExport?> LastAsync(Guid userId, CancellationToken ct) =>
        _db.DataExports.Where(e => e.UserId == userId).OrderByDescending(e => e.CreatedAt).FirstOrDefaultAsync(ct);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}

