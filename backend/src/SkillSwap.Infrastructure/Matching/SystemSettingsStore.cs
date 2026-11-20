using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Settings;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Matching;

public sealed class SystemSettingsStore : ISystemSettingsStore, ISystemSettingsAdminStore
{
    private readonly AppDbContext _db;

    public SystemSettingsStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct) =>
        await _db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<SystemSetting>> ListAsync(CancellationToken ct) => await _db.SystemSettings.ToListAsync(ct);

    public void Add(SystemSetting setting) => _db.SystemSettings.Add(setting);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
