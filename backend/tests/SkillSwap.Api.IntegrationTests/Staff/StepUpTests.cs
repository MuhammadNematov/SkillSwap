using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Application.Exceptions;
using SkillSwap.Application.Staff;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Staff;

/// <summary>
/// Подтверждение действия паролем и кодом 2FA (раздел 3 ТЗ, правило 7) на настоящих Identity и TOTP, а также таблица приглашений.
/// </summary>
[Collection(ApiCollection.Name)]
public class StepUpTests
{
    private readonly ApiFactory _factory;

    public StepUpTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private sealed record Staff(User User, string Key, IReadOnlyList<string> RecoveryCodes, AuthApi Api);

    /// <summary>Служебный аккаунт с паролем и включённой 2FA (ключ и резервные коды возвращаются).</summary>
    private async Task<Staff> StaffWithTwoFactorAsync(UserRole role = UserRole.Moderator, bool enableTwoFactor = true)
    {
        var user = User.CreateStaff(AuthApi.NewEmail(), role + " (step-up)", role);
        user.EmailConfirmed = true;
        using (var scope = _factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, AuthApi.Password);
            Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Code)));
        }

        var api = new AuthApi(_factory, TestIps.Next());
        if (!enableTwoFactor)
        {
            return new Staff(user, string.Empty, [], api);
        }

        var (key, codes) = await api.EnableTwoFactorAsync(TestTokens.ForUser(_factory, user));
        return new Staff(user, key, codes, api);
    }

    private async Task<T> WithServiceAsync<T>(Func<StepUpService, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<StepUpService>());
    }

    private Task<User> RequireAsync(Staff staff, string? password, string? code, bool recovery = false) =>
        WithServiceAsync(s => s.RequireAsync(staff.User.Id, new StepUpConfirmation(password, code, recovery), CancellationToken.None));

    private static async Task<string> CodeOfAsync(Func<Task> action) =>
        (await Assert.ThrowsAnyAsync<AppException>(action)).Code;

    private Task<int> FailuresAsync(Guid userId) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.ActorId == userId && a.Action == "StepUpFailed"));

    // ---------- успех ----------

    [Fact]
    public async Task The_right_password_and_a_current_code_confirm_the_action()
    {
        var staff = await StaffWithTwoFactorAsync();

        var confirmed = await RequireAsync(staff, AuthApi.Password, staff.Api.TotpCode(staff.Key));

        Assert.Equal(staff.User.Id, confirmed.Id);
        Assert.Equal(0, await FailuresAsync(staff.User.Id));
    }

    [Fact]
    public async Task Admins_and_superadmins_confirm_the_same_way()
    {
        foreach (var role in new[] { UserRole.Admin, UserRole.SuperAdmin })
        {
            var staff = await StaffWithTwoFactorAsync(role);

            var confirmed = await RequireAsync(staff, AuthApi.Password, staff.Api.TotpCode(staff.Key));

            Assert.Equal(role, confirmed.Role);
        }
    }

    [Fact]
    public async Task A_recovery_code_confirms_once_and_is_burned()
    {
        var staff = await StaffWithTwoFactorAsync();
        var code = staff.RecoveryCodes[0];

        await RequireAsync(staff, AuthApi.Password, code, recovery: true);
        var second = await CodeOfAsync(() => RequireAsync(staff, AuthApi.Password, code, recovery: true));

        Assert.Equal("invalid_two_factor_code", second);
    }

    // ---------- отказы ----------

    [Fact]
    public async Task A_confirmation_is_needed_every_time_a_used_code_does_not_work_twice()
    {
        var staff = await StaffWithTwoFactorAsync();
        var code = staff.Api.TotpCode(staff.Key);
        await RequireAsync(staff, AuthApi.Password, code);

        var replay = await CodeOfAsync(() => RequireAsync(staff, AuthApi.Password, code));   // «окна подтверждено недавно» нет

        Assert.Equal("invalid_two_factor_code", replay);
        Assert.Equal(1, await FailuresAsync(staff.User.Id));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "123456")]
    public async Task A_request_without_a_password_asks_for_the_confirmation(string? password, string? code)
    {
        var staff = await StaffWithTwoFactorAsync();

        var error = await CodeOfAsync(() => RequireAsync(staff, password, code));

        Assert.Equal("step_up_required", error);
    }

    [Fact]
    public async Task A_missing_confirmation_object_asks_for_it()
    {
        var staff = await StaffWithTwoFactorAsync();

        var error = await CodeOfAsync(() => WithServiceAsync(s => s.RequireAsync(staff.User.Id, null, CancellationToken.None)));

        Assert.Equal("step_up_required", error);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_counted_and_audited_without_the_secret()
    {
        var staff = await StaffWithTwoFactorAsync();

        var error = await CodeOfAsync(() => RequireAsync(staff, "wrong-password-1", staff.Api.TotpCode(staff.Key)));

        Assert.Equal("invalid_password", error);
        Assert.Equal(1, (await _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == staff.User.Id))).AccessFailedCount);
        var audit = await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.ActorId == staff.User.Id && a.Action == "StepUpFailed"));
        Assert.Contains("invalid_password", audit.DetailsJson);
        Assert.DoesNotContain("wrong-password-1", audit.DetailsJson);
    }

    [Fact]
    public async Task The_password_alone_is_not_enough_the_code_is_required_and_checked()
    {
        var staff = await StaffWithTwoFactorAsync();

        var missing = await CodeOfAsync(() => RequireAsync(staff, AuthApi.Password, null));
        var wrong = await CodeOfAsync(() => RequireAsync(staff, AuthApi.Password, "000000"));

        Assert.Equal("two_factor_code_required", missing);
        Assert.Equal("invalid_two_factor_code", wrong);
        Assert.Equal(1, await FailuresAsync(staff.User.Id));   // отсутствие кода ошибкой попытки не считается, неверный код считается
    }

    [Fact]
    public async Task An_account_without_two_factor_cannot_confirm_anything()
    {
        var staff = await StaffWithTwoFactorAsync(enableTwoFactor: false);

        var error = await CodeOfAsync(() => RequireAsync(staff, AuthApi.Password, "123456"));

        Assert.Equal("two_factor_not_enabled", error);
    }

    [Fact]
    public async Task A_series_of_wrong_passwords_locks_the_account_and_even_the_right_one_is_then_refused()
    {
        var staff = await StaffWithTwoFactorAsync();
        var codes = new List<string>();
        for (var i = 0; i < 15 && !codes.Contains("account_locked_out"); i++)
        {
            codes.Add(await CodeOfAsync(() => RequireAsync(staff, "wrong-password-1", "000000")));
        }

        Assert.Contains("account_locked_out", codes);
        var right = await CodeOfAsync(() => RequireAsync(staff, AuthApi.Password, staff.Api.TotpCode(staff.Key)));
        Assert.Equal("account_locked_out", right);
    }

    [Theory]
    [InlineData(UserStatus.Blocked)]
    [InlineData(UserStatus.Deleted)]
    public async Task Only_an_active_staff_account_can_confirm(UserStatus status)
    {
        var staff = await StaffWithTwoFactorAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == staff.User.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, status));
            return 0;
        });

        var error = await CodeOfAsync(() => RequireAsync(staff, AuthApi.Password, staff.Api.TotpCode(staff.Key)));

        Assert.Equal("staff_required", error);
    }

    [Fact]
    public async Task A_regular_user_and_an_unknown_id_are_refused()
    {
        var person = User.CreateRegular(AuthApi.NewEmail(), "Обычный", new DateOnly(1995, 1, 1), "ru");
        using (var scope = _factory.Services.CreateScope())
        {
            Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(person, AuthApi.Password)).Succeeded);
        }

        var regular = new Staff(person, "", [], new AuthApi(_factory));

        var forRegular = await CodeOfAsync(() => RequireAsync(regular, AuthApi.Password, "123456"));
        var forUnknown = await CodeOfAsync(() => WithServiceAsync(s => s.RequireAsync(Guid.NewGuid(), new StepUpConfirmation(AuthApi.Password, "123456"), CancellationToken.None)));

        Assert.Equal("staff_required", forRegular);
        Assert.Equal("staff_required", forUnknown);
    }

    // ---------- таблица приглашений ----------

    [Fact]
    public async Task An_invitation_is_stored_by_the_hash_of_its_token_and_the_hash_is_unique()
    {
        var staff = await StaffWithTwoFactorAsync(UserRole.Admin);
        var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var now = _factory.Clock.GetUtcNow().UtcDateTime;
        var invitation = StaffInvitation.Create("Future.Moderator@Example.com", UserRole.Moderator, staff.User.Id, hash, now);
        await _factory.QueryAsync(async db =>
        {
            db.StaffInvitations.Add(invitation);
            await db.SaveChangesAsync();
            return 0;
        });

        var stored = await _factory.QueryAsync(db => db.StaffInvitations.AsNoTracking().SingleAsync(i => i.TokenHash == hash));

        Assert.Equal("future.moderator@example.com", stored.Email);
        Assert.Equal(UserRole.Moderator, stored.TargetRole);
        Assert.Equal(StaffInvitationStatus.Pending, stored.StatusAt(now));
        var duplicate = StaffInvitation.Create("other@example.com", UserRole.Moderator, staff.User.Id, hash, now);
        var attempt = await Record.ExceptionAsync(() => _factory.QueryAsync(async db =>
        {
            db.StaffInvitations.Add(duplicate);
            await db.SaveChangesAsync();
            return 0;
        }));
        Assert.IsType<DbUpdateException>(attempt);
    }
}
