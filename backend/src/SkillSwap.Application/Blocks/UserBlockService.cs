using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Blocks;

/// <summary>Блокировка пользователей (раздел 12 ТЗ): POST и DELETE /users/{id}/block, список заблокированных.</summary>
public sealed class UserBlockService
{
    private readonly IProfileStore _profiles;
    private readonly IUserBlockStore _blocks;

    public UserBlockService(IProfileStore profiles, IUserBlockStore blocks)
    {
        _profiles = profiles;
        _blocks = blocks;
    }

    public async Task BlockAsync(Guid blockerId, Guid targetId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(blockerId, ct);

        if (blockerId == targetId)
        {
            throw new BadRequestException("cannot_block_self", "Нельзя заблокировать самого себя.");
        }

        // Служебные аккаунты и удалённые пользователи для обычного пользователя «не существуют».
        var target = await _profiles.FindUserAsync(targetId, ct);
        if (target is null || !target.IsRegular || target.Status == UserStatus.Deleted)
        {
            throw new NotFoundException("user_not_found", "Пользователь не найден.");
        }

        switch (await _blocks.AddAsync(blockerId, targetId, ct))
        {
            case BlockOutcome.TooMany:
                throw new ConflictException("too_many_blocks", $"Можно заблокировать не больше {UserBlock.MaxPerUser} пользователей.");
            case BlockOutcome.TargetGone:
                throw new NotFoundException("user_not_found", "Пользователь не найден.");
        }
    }

    public async Task UnblockAsync(Guid blockerId, Guid targetId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(blockerId, ct);
        await _blocks.RemoveAsync(blockerId, targetId, ct);
    }

    public async Task<IReadOnlyList<BlockedUserDto>> ListAsync(Guid blockerId, CancellationToken ct)
    {
        await RequireActiveRegularAsync(blockerId, ct);
        return await _blocks.ListAsync(blockerId, ct);
    }

    private async Task RequireActiveRegularAsync(Guid userId, CancellationToken ct)
    {
        var user = await _profiles.FindUserAsync(userId, ct) ?? throw new NotFoundException("user_not_found", "Пользователь не найден.");
        if (!user.IsRegular)
        {
            throw new ForbiddenException("regular_account_required", "Блокировки доступны только обычным аккаунтам.");
        }

        if (user.Status != UserStatus.Active)
        {
            throw new ForbiddenException("account_not_active", "Аккаунт недоступен.");
        }
    }
}
