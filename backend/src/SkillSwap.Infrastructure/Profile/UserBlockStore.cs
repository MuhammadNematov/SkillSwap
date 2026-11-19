using Microsoft.EntityFrameworkCore;
using Npgsql;
using SkillSwap.Application.Blocks;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Infrastructure.Profile;

public sealed class UserBlockStore : IUserBlockStore
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public UserBlockStore(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<BlockOutcome> AddAsync(Guid blockerId, Guid targetId, CancellationToken ct)
    {
        try
        {
            if (await _db.UserBlocks.AnyAsync(b => b.UserId == blockerId && b.BlockedUserId == targetId, ct))
            {
                return BlockOutcome.AlreadyBlocked;
            }

            if (await _db.UserBlocks.CountAsync(b => b.UserId == blockerId, ct) >= UserBlock.MaxPerUser)
            {
                return BlockOutcome.TooMany;
            }

            _db.UserBlocks.Add(UserBlock.Create(blockerId, targetId, _clock.GetUtcNow().UtcDateTime));
            await _db.SaveChangesAsync(ct);
            return BlockOutcome.Added;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return BlockOutcome.AlreadyBlocked;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            return BlockOutcome.TargetGone;
        }
    }

    public async Task RemoveAsync(Guid blockerId, Guid targetId, CancellationToken ct) =>
        await _db.UserBlocks.Where(b => b.UserId == blockerId && b.BlockedUserId == targetId).ExecuteDeleteAsync(ct);

    public async Task<IReadOnlyList<BlockedUserDto>> ListAsync(Guid blockerId, CancellationToken ct) =>
        await (from b in _db.UserBlocks.AsNoTracking()
               join u in _db.Users.AsNoTracking() on b.BlockedUserId equals u.Id
               where b.UserId == blockerId && u.Status != UserStatus.Deleted
               orderby b.CreatedAt descending, u.Id
               select new BlockedUserDto(u.Id, u.Name, u.AvatarKey, b.CreatedAt))
            .ToListAsync(ct);

    public async Task RemoveAllForUserAsync(Guid userId, CancellationToken ct) =>
        await _db.UserBlocks.Where(b => b.UserId == userId || b.BlockedUserId == userId).ExecuteDeleteAsync(ct);

    public async Task<IReadOnlySet<Guid>> BlockedEitherWayAmongAsync(Guid userId, IReadOnlyCollection<Guid> otherIds, CancellationToken ct)
    {
        var asBlocker = _db.UserBlocks.AsNoTracking().Where(b => b.UserId == userId && otherIds.Contains(b.BlockedUserId)).Select(b => b.BlockedUserId);
        var asBlocked = _db.UserBlocks.AsNoTracking().Where(b => b.BlockedUserId == userId && otherIds.Contains(b.UserId)).Select(b => b.UserId);
        return (await asBlocker.Union(asBlocked).ToListAsync(ct)).ToHashSet();
    }

    public Task<bool> IsBlockedEitherWayAsync(Guid userA, Guid userB, CancellationToken ct) =>
        _db.UserBlocks.AsNoTracking().AnyAsync(b =>
            (b.UserId == userA && b.BlockedUserId == userB) || (b.UserId == userB && b.BlockedUserId == userA), ct);
}
