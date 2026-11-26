using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Telegram;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Telegram;

public sealed class TelegramStore : ITelegramStore
{
    private readonly AppDbContext _db;

    public TelegramStore(AppDbContext db)
    {
        _db = db;
    }

    public Task<TelegramLink?> FindByUserAsync(Guid userId, CancellationToken ct) => _db.TelegramLinks.FirstOrDefaultAsync(l => l.UserId == userId, ct);

    public Task<TelegramLink?> FindByChatAsync(long chatId, CancellationToken ct) => _db.TelegramLinks.FirstOrDefaultAsync(l => l.ChatId == chatId, ct);

    public void Add(TelegramLink link) => _db.TelegramLinks.Add(link);

    public void Remove(TelegramLink link) => _db.TelegramLinks.Remove(link);

    public Task<TelegramLinkCode?> FindCodeAsync(string codeHash, CancellationToken ct) => _db.TelegramLinkCodes.FirstOrDefaultAsync(c => c.CodeHash == codeHash, ct);

    public void Add(TelegramLinkCode code) => _db.TelegramLinkCodes.Add(code);

    public Task DeleteCodesAsync(Guid userId, CancellationToken ct) => _db.TelegramLinkCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);

    public async Task<IReadOnlyList<NotificationPreference>> ListPreferencesAsync(Guid userId, CancellationToken ct) =>
        await _db.NotificationPreferences.Where(p => p.UserId == userId).ToListAsync(ct);

    public void Add(NotificationPreference preference) => _db.NotificationPreferences.Add(preference);

    public async Task<bool> IsEnabledAsync(Guid userId, NotificationCategory category, NotificationChannel channel, CancellationToken ct) =>
        await _db.NotificationPreferences.AsNoTracking()
            .Where(p => p.UserId == userId && p.Category == category && p.Channel == channel)
            .Select(p => (bool?)p.Enabled)
            .FirstOrDefaultAsync(ct) ?? true;

    // Одним оператором: вставить отметку, а если она есть — обновить, только когда окно прошло. Параллельные вызовы получат строку не больше одного раза.
    public async Task<bool> TryAcquireDialogSlotAsync(Guid userId, Guid exchangeId, DateTime now, TimeSpan interval, CancellationToken ct)
    {
        var threshold = now - interval;
        var rows = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "TelegramDialogThrottles" ("UserId", "ExchangeId", "LastSentAt") VALUES ({userId}, {exchangeId}, {now})
             ON CONFLICT ("UserId", "ExchangeId") DO UPDATE SET "LastSentAt" = EXCLUDED."LastSentAt"
             WHERE "TelegramDialogThrottles"."LastSentAt" <= {threshold}
             """, ct);
        return rows > 0;
    }

    public async Task DeleteUserDataAsync(Guid userId, CancellationToken ct)
    {
        await _db.TelegramLinks.Where(l => l.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.TelegramLinkCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.NotificationPreferences.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await _db.TelegramDialogThrottles.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
    }

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
