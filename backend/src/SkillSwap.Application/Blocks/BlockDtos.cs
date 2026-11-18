namespace SkillSwap.Application.Blocks;

/// <summary>Пользователь из моего списка заблокированных.</summary>
public sealed record BlockedUserDto(Guid UserId, string Name, string? AvatarUrl, DateTime BlockedAt);

public enum BlockOutcome
{
    Added,

    /// <summary>Уже был заблокирован: повторный запрос ничего не меняет.</summary>
    AlreadyBlocked,

    TooMany,

    /// <summary>Пользователя не стало, пока шёл запрос.</summary>
    TargetGone,
}
