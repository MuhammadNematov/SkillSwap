using SkillSwap.Application.Exceptions;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Staff;

/// <summary>
/// Правила управления служебными аккаунтами (раздел 3 ТЗ, правила 4 и 6). Чистые проверки без обращения к базе: числа и роли передаёт вызывающий сервис,
/// который держит нужные блокировки.
/// </summary>
public static class StaffRules
{
    /// <summary>Можно ли роли <paramref name="actor"/> приглашать и снимать <paramref name="target"/>: модераторов — админ и суперадмин, админов — только суперадмин.</summary>
    public static bool CanManage(UserRole actor, UserRole target) => (actor, target) switch
    {
        (UserRole.Admin or UserRole.SuperAdmin, UserRole.Moderator) => true,
        (UserRole.SuperAdmin, UserRole.Admin) => true,
        _ => false,
    };

    /// <summary>Роли, которые <paramref name="actor"/> вправе назначать (для списка в интерфейсе).</summary>
    public static IReadOnlyList<UserRole> ManageableRoles(UserRole actor) => actor switch
    {
        UserRole.SuperAdmin => [UserRole.Moderator, UserRole.Admin],
        UserRole.Admin => [UserRole.Moderator],
        _ => [],
    };

    /// <summary>Нет прав приглашать или снимать эту роль: 403 <c>staff_role_not_allowed</c> (админы назначаются только суперадмином, суперадмина по приглашению не назначить).</summary>
    public static void RequireCanManage(UserRole actor, UserRole target)
    {
        if (!CanManage(actor, target))
        {
            throw new ForbiddenException("staff_role_not_allowed", target == UserRole.Admin
                ? "Админов приглашает и снимает только суперадмин."
                : "У вас нет прав управлять этой ролью.");
        }
    }

    /// <summary>
    /// Кто вправе снимать привязку личного аккаунта: админ только у модераторов, суперадмин у модераторов, админов и других суперадминов. Сотрудник сам себе привязку снять не может
    /// (это проверяет <see cref="RequireNotSelf"/>).
    /// </summary>
    public static bool CanUnlinkPersonal(UserRole actor, UserRole target) => (actor, target) switch
    {
        (UserRole.Admin or UserRole.SuperAdmin, UserRole.Moderator) => true,
        (UserRole.SuperAdmin, UserRole.Admin or UserRole.SuperAdmin) => true,
        _ => false,
    };

    /// <summary>Никто не меняет права самому себе: 403 <c>cannot_change_own_rights</c>.</summary>
    public static void RequireNotSelf(Guid actorId, Guid targetId)
    {
        if (actorId == targetId)
        {
            throw new ForbiddenException("cannot_change_own_rights", "Нельзя менять права самому себе.");
        }
    }

    /// <summary>
    /// Нельзя снять или заблокировать последнего действующего суперадмина: 409 <c>last_superadmin</c>. <paramref name="activeSuperAdmins"/> — сколько их
    /// сейчас (с целью включительно); цель — суперадмин и он единственный.
    /// </summary>
    public static void RequireNotLastSuperAdmin(UserRole targetRole, int activeSuperAdmins)
    {
        if (targetRole == UserRole.SuperAdmin && activeSuperAdmins <= 1)
        {
            throw new ConflictException("last_superadmin", "Нельзя снять или заблокировать последнего суперадмина.");
        }
    }
}
