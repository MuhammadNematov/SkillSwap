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
               select new BlockedUserDto(u.Id, u.Name, u.AvatarUrl, b.CreatedAt))
            .ToListAsync(ct);

    public Task<bool> IsBlockedEitherWayAsync(Guid userA, Guid userB, CancellationToken ct) =>
        _db.UserBlocks.AsNoTracking().AnyAsync(b =>
            (b.UserId == userA && b.BlockedUserId == userB) || (b.UserId == userB && b.BlockedUserId == userA), ct);
}
