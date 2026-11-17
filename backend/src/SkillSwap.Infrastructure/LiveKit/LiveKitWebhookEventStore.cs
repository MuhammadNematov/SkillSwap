using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.LiveKit;

public sealed class LiveKitWebhookEventStore : ILiveKitWebhookEventStore
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public LiveKitWebhookEventStore(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Один INSERT с «ON CONFLICT DO NOTHING» по уникальному EventId: проверка «уже было?» и запись — одна атомарная
    /// операция. Две одновременные доставки одного события не создадут дубль и не вызовут исключения (в отличие от
    /// «найти, а потом вставить» или «вставить и поймать нарушение уникальности»).
    /// </summary>
    public async Task<bool> TryAddAsync(LiveKitWebhookEvent e, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var inserted = await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "LiveKitWebhookEvents"
                ("Id", "EventId", "Type", "RoomName", "ParticipantIdentity", "OccurredAt", "ProcessedAt", "CreatedAt", "UpdatedAt")
            VALUES
                ({e.Id}, {e.EventId}, {e.Type}, {e.RoomName}, {e.ParticipantIdentity}, {e.OccurredAt}, NULL, {now}, {now})
            ON CONFLICT ("EventId") DO NOTHING
            """, ct);

        return inserted == 1;
    }
}
