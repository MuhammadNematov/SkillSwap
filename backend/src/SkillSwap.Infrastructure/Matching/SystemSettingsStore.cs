using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Settings;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Matching;

public sealed class SystemSettingsStore : ISystemSettingsStore
{
    private readonly AppDbContext _db;

    public SystemSettingsStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct) =>
        await _db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
}
