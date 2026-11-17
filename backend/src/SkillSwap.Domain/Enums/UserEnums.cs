namespace SkillSwap.Domain.Enums;

/// <summary>Тип учётной записи: обычный пользователь или служебный аккаунт (раздел 3 ТЗ).</summary>
public enum AccountType
{
    Regular,
    Staff,
}

/// <summary>Роль в системе (раздел 3 ТЗ).</summary>
public enum UserRole
{
    User,
    Moderator,
    Admin,
    SuperAdmin,
}

public enum UserStatus
{
    Active,
    Blocked,
    Deleted,
}
