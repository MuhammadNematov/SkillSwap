using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Domain.Exceptions;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Удаление аккаунта и выгрузка данных (раздел 8.1 ТЗ) на уровне домена.</summary>
public class AccountLifecycleTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private static User Person()
    {
        var user = User.CreateRegular("a@example.com", "Алиса", new DateOnly(1995, 3, 4), "ru");
        user.UpdateProfile("Алиса", "О себе", "ru", "Asia/Dushanbe", true);
        user.AvatarKey = "avatars/x/y.webp";
        return user;
    }

    [Fact]
    public void Deleting_wipes_personal_data_and_hides_the_person()
    {
        var user = Person();

        user.MarkDeleted(Now, keepPersonalData: false);

        Assert.Equal(UserStatus.Deleted, user.Status);
        Assert.Equal(Now, user.DeletedAt);
        Assert.Equal(User.DeletedDisplayName, user.Name);
        Assert.Null(user.Bio);
        Assert.Null(user.DateOfBirth);
        Assert.Null(user.AvatarKey);
        Assert.False(user.IsSearchable);
        Assert.False(user.ErasurePending);
        Assert.Equal("UTC", user.TimeZone);
    }

    [Fact]
    public void An_open_case_keeps_the_data_and_marks_the_erasure_pending_until_it_is_wiped()
    {
        var user = Person();

        user.MarkDeleted(Now, keepPersonalData: true);

        Assert.Equal(UserStatus.Deleted, user.Status);
        Assert.True(user.ErasurePending);
        Assert.Equal("Алиса", user.Name);
        Assert.Equal("О себе", user.Bio);
        Assert.NotNull(user.DateOfBirth);
        Assert.False(user.IsSearchable);

        user.WipePersonalData();

        Assert.False(user.ErasurePending);
        Assert.Equal(User.DeletedDisplayName, user.Name);
        Assert.Null(user.Bio);
        Assert.Null(user.DateOfBirth);
    }

    [Fact]
    public void The_first_deletion_time_is_kept_when_called_again()
    {
        var user = Person();
        user.MarkDeleted(Now, false);

        user.MarkDeleted(Now.AddDays(3), false);

        Assert.Equal(Now, user.DeletedAt);
    }

    [Fact]
    public void A_staff_account_cannot_be_deleted()
    {
        var staff = User.CreateStaff("m@example.com", "Модератор", UserRole.Moderator);

        Assert.Equal("staff_cannot_delete_account", Assert.Throws<DomainValidationException>(() => staff.MarkDeleted(Now, false)).Code);
        Assert.Equal(UserStatus.Active, staff.Status);
    }

    // ---------- обмен ----------

    private static Exchange NewExchange() => Exchange.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 30, Now);

    [Fact]
    public void Deleting_an_account_aborts_active_and_frozen_exchanges_without_a_culprit()
    {
        var active = NewExchange();
        var frozen = NewExchange();
        frozen.Freeze(Now);

        active.AbortByDeletion(Now.AddDays(1));
        frozen.AbortByDeletion(Now.AddDays(1));

        foreach (var e in new[] { active, frozen })
        {
            Assert.Equal(ExchangeStatus.Aborted, e.Status);
            Assert.Null(e.AbortedById);
            Assert.Null(e.BlockedById);
            Assert.Equal(Exchange.AbortReasonAccountDeleted, e.AbortReason);
            Assert.Equal(Now.AddDays(1), e.ClosedAt);
        }
    }

    [Fact]
    public void A_finished_exchange_is_not_touched_by_deletion()
    {
        var done = NewExchange();
        done.AbortByBlock(Guid.NewGuid(), Now);

        Assert.Throws<InvalidStateTransitionException>(() => done.AbortByDeletion(Now));
    }

    // ---------- выгрузка ----------

    [Fact]
    public void An_export_goes_pending_processing_ready_and_expires_after_24_hours()
    {
        var export = DataExport.Request(Guid.NewGuid());
        Assert.Equal(DataExportStatus.Pending, export.Status);

        Assert.True(export.Start());
        Assert.False(export.Start());   // повторный запуск уже начатой выгрузки ничего не делает
        export.Complete(1234, Now);

        Assert.Equal(DataExportStatus.Ready, export.Status);
        Assert.Equal(1234, export.SizeBytes);
        Assert.Equal(Now.AddHours(24), export.ExpiresAt);
        Assert.Equal($"exports/{export.UserId:N}/{export.Id:N}.json", export.StorageKey);

        Assert.False(export.ExpireIfDue(Now.AddHours(24).AddTicks(-1)));
        Assert.True(export.ExpireIfDue(Now.AddHours(24)));
        Assert.Equal(DataExportStatus.Expired, export.Status);
        Assert.False(export.ExpireIfDue(Now.AddDays(5)));
    }

    [Fact]
    public void Only_a_ready_export_can_expire()
    {
        var pending = DataExport.Request(Guid.NewGuid());
        var failed = DataExport.Request(Guid.NewGuid());
        failed.Start();
        failed.Fail();

        Assert.False(pending.ExpireIfDue(Now.AddDays(9)));
        Assert.False(failed.ExpireIfDue(Now.AddDays(9)));
        Assert.Equal(DataExportStatus.Failed, failed.Status);
    }
}
