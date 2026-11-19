using Microsoft.EntityFrameworkCore;
using SkillSwap.Application.Abstractions;
using SkillSwap.Domain.Entities;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Auth;

public sealed class EmailCodeStore : IEmailCodeStore
{
    private readonly AppDbContext _db;

    public EmailCodeStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task SetAsync(Guid userId, string codeHash, DateTime expiresAt, CancellationToken ct = default) =>
        await _db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.EmailCodeHash, codeHash)
            .SetProperty(u => u.EmailCodeExpiresAt, expiresAt)
            .SetProperty(u => u.EmailCodeAttempts, 0), ct);

    /// <summary>Один UPDATE … RETURNING: проверка «код действует и попытки остались» и списание попытки неделимы.</summary>
    public async Task<string?> TakeAttemptAsync(Guid userId, DateTime now, int maxAttempts, CancellationToken ct = default) =>
        (await _db.Database.SqlQuery<string>($"""
            UPDATE "Users"
            SET "EmailCodeAttempts" = "EmailCodeAttempts" + 1
            WHERE "Id" = {userId}
              AND "EmailCodeHash" IS NOT NULL
              AND "EmailCodeExpiresAt" > {now}
              AND "EmailCodeAttempts" < {maxAttempts}
            RETURNING "EmailCodeHash" AS "Value"
            """).ToListAsync(ct)).SingleOrDefault();

    public async Task ClearAsync(Guid userId, CancellationToken ct = default) =>
        await _db.Users.Where(u => u.Id == userId && u.EmailCodeHash != null).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.EmailCodeHash, (string?)null)
            .SetProperty(u => u.EmailCodeExpiresAt, (DateTime?)null)
            .SetProperty(u => u.EmailCodeAttempts, 0), ct);
}
