using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Moderation;

/// <summary>Адрес клиента по умолчанию: его нет (фоновые задачи, тесты без запроса). Api заменяет его адресом из HTTP-запроса.</summary>
public sealed class NullRequestInfo : IRequestInfo
{
    public string? IpAddress => null;
}

public sealed class AuditLogWriter : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IRequestInfo _request;

    public AuditLogWriter(AppDbContext db, TimeProvider clock, IRequestInfo request)
    {
        _db = db;
        _clock = clock;
        _request = request;
    }

    public void Add(AuditEntry entry) => _db.AuditLogs.Add(ToEntity(entry));

    public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        Add(entry);
        await _db.SaveChangesAsync(ct);
    }

    private AuditLog ToEntity(AuditEntry e) => AuditLog.Create(
        e.ActorId, e.Action, e.TargetType, e.TargetId,
        e.Details is null ? null : JsonSerializer.Serialize(e.Details, Json),
        _request.IpAddress, _clock.GetUtcNow().UtcDateTime);
}

public sealed class ChatAccessGrantStore : IChatAccessGrantStore
{
    private readonly AppDbContext _db;

    public ChatAccessGrantStore(AppDbContext db)
    {
        _db = db;
    }

    public void Add(ChatAccessGrant grant) => _db.ChatAccessGrants.Add(grant);

    public Task<ChatAccessGrant?> FindActiveAsync(Guid moderatorId, Guid exchangeId, CancellationToken ct) =>
        _db.ChatAccessGrants.AsNoTracking().FirstOrDefaultAsync(g => g.ModeratorId == moderatorId && g.ExchangeId == exchangeId && g.RevokedAt == null, ct);

    public async Task<IReadOnlyList<ChatAccessGrant>> ListActiveForCaseAsync(Guid caseId, CancellationToken ct) =>
        await _db.ChatAccessGrants.Where(g => g.CaseId == caseId && g.RevokedAt == null).ToListAsync(ct);

    public Task SaveAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
