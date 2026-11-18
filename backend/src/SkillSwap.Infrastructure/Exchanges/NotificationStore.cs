using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Notifications;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Exchanges;

public sealed class NotificationStore : INotificationStore
{
    private readonly AppDbContext _db;

    public NotificationStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(Notification notification) => _db.Notifications.Add(notification);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);

    public Task<Notification?> FindAsync(Guid id, CancellationToken ct) => _db.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);

    public async Task<(IReadOnlyList<Notification> Items, int Total)> ListAsync(Guid userId, bool unreadOnly, int page, int pageSize, CancellationToken ct)
    {
        var query = _db.Notifications.AsNoTracking().Where(n => n.UserId == userId && (!unreadOnly || !n.IsRead));
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public Task<int> CountUnreadAsync(Guid userId, CancellationToken ct) =>
        _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead, ct);

    public Task<int> MarkAllReadAsync(Guid userId, DateTime now, CancellationToken ct) =>
        _db.Notifications.Where(n => n.UserId == userId && !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true).SetProperty(n => n.ReadAt, now).SetProperty(n => n.UpdatedAt, now), ct);
}
