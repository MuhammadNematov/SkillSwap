using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Application.Tests;

/// <summary>Правила управления служебными аккаунтами (раздел 3 ТЗ, правила 4 и 6).</summary>
public class StaffRulesTests
{
    [Theory]
    [InlineData(UserRole.Admin, UserRole.Moderator, true)]
    [InlineData(UserRole.SuperAdmin, UserRole.Moderator, true)]
    [InlineData(UserRole.SuperAdmin, UserRole.Admin, true)]
    [InlineData(UserRole.Admin, UserRole.Admin, false)]          // админов приглашает и снимает только суперадмин
    [InlineData(UserRole.Admin, UserRole.SuperAdmin, false)]
    [InlineData(UserRole.SuperAdmin, UserRole.SuperAdmin, false)]
    [InlineData(UserRole.Moderator, UserRole.Moderator, false)]
    [InlineData(UserRole.Moderator, UserRole.Admin, false)]
    [InlineData(UserRole.User, UserRole.Moderator, false)]
    public void Who_may_manage_whom(UserRole actor, UserRole target, bool allowed)
    {
        Assert.Equal(allowed, StaffRules.CanManage(actor, target));
        if (allowed)
        {
            StaffRules.RequireCanManage(actor, target);
        }
        else
        {
            Assert.Equal("staff_role_not_allowed", Assert.Throws<ForbiddenException>(() => StaffRules.RequireCanManage(actor, target)).Code);
        }
    }

    [Fact]
    public void The_list_of_manageable_roles_matches_the_rules()
    {
        Assert.Equal([UserRole.Moderator, UserRole.Admin], StaffRules.ManageableRoles(UserRole.SuperAdmin));
        Assert.Equal([UserRole.Moderator], StaffRules.ManageableRoles(UserRole.Admin));
        Assert.Empty(StaffRules.ManageableRoles(UserRole.Moderator));
        Assert.Empty(StaffRules.ManageableRoles(UserRole.User));
    }

    [Fact]
    public void Nobody_changes_their_own_rights()
    {
        var me = Guid.NewGuid();

        var error = Assert.Throws<ForbiddenException>(() => StaffRules.RequireNotSelf(me, me));

        Assert.Equal("cannot_change_own_rights", error.Code);
        StaffRules.RequireNotSelf(me, Guid.NewGuid());
    }

    [Theory]
    [InlineData(UserRole.SuperAdmin, 1, true)]
    [InlineData(UserRole.SuperAdmin, 0, true)]
    [InlineData(UserRole.SuperAdmin, 2, false)]
    [InlineData(UserRole.Admin, 1, false)]       // правило только про суперадмина
    [InlineData(UserRole.Moderator, 1, false)]
    public void The_last_active_superadmin_cannot_be_removed(UserRole target, int activeSuperAdmins, bool refused)
    {
        if (refused)
        {
            Assert.Equal("last_superadmin", Assert.Throws<ConflictException>(() => StaffRules.RequireNotLastSuperAdmin(target, activeSuperAdmins)).Code);
        }
        else
        {
            StaffRules.RequireNotLastSuperAdmin(target, activeSuperAdmins);
        }
    }

    [Theory]
    [InlineData(UserRole.Admin, UserRole.Moderator, true)]
    [InlineData(UserRole.SuperAdmin, UserRole.Moderator, true)]
    [InlineData(UserRole.SuperAdmin, UserRole.Admin, true)]
    [InlineData(UserRole.SuperAdmin, UserRole.SuperAdmin, true)]
    [InlineData(UserRole.Admin, UserRole.Admin, false)]
    [InlineData(UserRole.Admin, UserRole.SuperAdmin, false)]
    [InlineData(UserRole.Moderator, UserRole.Moderator, false)]
    [InlineData(UserRole.User, UserRole.Moderator, false)]
    public void Who_may_unlink_a_personal_account(UserRole actor, UserRole target, bool allowed)
    {
        Assert.Equal(allowed, StaffRules.CanUnlinkPersonal(actor, target));
    }
}
