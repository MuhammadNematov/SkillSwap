using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SkillSwap.Api.Auth;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Persistence;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>
/// Основа этапа 6 на настоящей БД: политики служебных ролей, доступ к чату только участникам и модератору с активным доступом, журнал аудита только на добавление
/// и ограничения таблиц отзывов и обращений (уникальность, проверки, внешние ключи, оптимистичная блокировка).
/// </summary>
[Collection(ApiCollection.Name)]
public class ModerationFoundationTests
{
    private static readonly string Text = "Подробное описание не короче двадцати символов.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ModerationFoundationTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private async Task<Case> OpenCaseAsync(Guid exchangeId, Guid openedBy, Guid against, CaseType type = CaseType.Conduct)
    {
        var c = Case.Open(type, CaseRules.CategoriesFor(type)[0], exchangeId, null, openedBy, against, Text, false, false, Now);
        await _factory.QueryAsync(async db =>
        {
            db.Cases.Add(c);
            await db.SaveChangesAsync();
            return 0;
        });
        return c;
    }

    private static async Task<T> InScopeAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> action)
    {
        using var scope = services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private async Task<bool> AllowedAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, object policyOrRequirement) =>
        await InScopeAsync(_factory.Services, async sp =>
        {
            var auth = sp.GetRequiredService<IAuthorizationService>();
            var result = policyOrRequirement is string policy
                ? await auth.AuthorizeAsync(user, resource, policy)
                : await auth.AuthorizeAsync(user, resource, (IAuthorizationRequirement)policyOrRequirement);
            return result.Succeeded;
        });

    // ---------- политики служебных ролей ----------

    [Theory]
    [InlineData(UserRole.Moderator, true, false, true)]
    [InlineData(UserRole.Admin, false, true, true)]
    [InlineData(UserRole.SuperAdmin, false, true, true)]
    [InlineData(UserRole.User, false, false, false)]
    public async Task Each_staff_role_passes_exactly_its_own_policies(UserRole role, bool moderator, bool admin, bool staff)
    {
        var user = role == UserRole.User
            ? User.CreateRegular(AuthApi.NewEmail(), "Обычный", new DateOnly(1995, 1, 1), "ru")
            : User.CreateStaff(AuthApi.NewEmail(), "Служебный", role);
        var principal = ModerationTestData.PrincipalFor(user);

        Assert.Equal(moderator, await AllowedAsync(principal, null, Policies.Moderator));
        Assert.Equal(admin, await AllowedAsync(principal, null, Policies.Admin));
        Assert.Equal(staff, await AllowedAsync(principal, null, Policies.Staff));
    }

    [Theory]
    [InlineData(Policies.Moderator)]
    [InlineData(Policies.Admin)]
    [InlineData(Policies.Staff)]
    public async Task An_anonymous_caller_passes_no_staff_policy(string policy)
    {
        Assert.False(await AllowedAsync(ModerationTestData.Anonymous(), null, policy));
    }

    // ---------- доступ к чату ----------

    private async Task<(MatchPerson A, MatchPerson B, Guid ExchangeId, Case Case)> ChatWorldAsync()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        return (a, b, exchangeId, await OpenCaseAsync(exchangeId, a.Id, b.Id));
    }

    private async Task GrantAsync(Case c, Guid moderatorId, Guid exchangeId, bool revoked = false)
    {
        var grant = ChatAccessGrant.Create(c.Id, moderatorId, exchangeId, GrantReason.Case, Now);
        if (revoked)
        {
            grant.Revoke(Now.AddMinutes(5));
        }

        await _factory.QueryAsync(async db =>
        {
            db.ChatAccessGrants.Add(grant);
            await db.SaveChangesAsync();
            return 0;
        });
    }

    private Task<bool> CanReadAsync(User user, Guid exchangeId) =>
        InScopeAsync(_factory.Services, sp => sp.GetRequiredService<ChatAccessPolicy>().CanReadAsync(user.Id, user.Role, exchangeId, CancellationToken.None));

    private Task<bool> HandlerAllowsAsync(User user, Guid exchangeId) =>
        AllowedAsync(ModerationTestData.PrincipalFor(user), exchangeId, new ChatAccessRequirement());

    [Fact]
    public async Task Both_participants_read_their_chat_and_a_stranger_does_not()
    {
        var (a, b, exchangeId, _) = await ChatWorldAsync();
        var stranger = (await MatchTestWorld.CreateAsync(_factory)).UserAsync(
            (0, SkillLevel.Beginner), (1, SkillLevel.Beginner)).GetAwaiter().GetResult();

        foreach (var who in new[] { a, b })
        {
            Assert.True(await CanReadAsync(who.User, exchangeId));
            Assert.True(await HandlerAllowsAsync(who.User, exchangeId));
        }

        Assert.False(await CanReadAsync(stranger.User, exchangeId));
        Assert.False(await HandlerAllowsAsync(stranger.User, exchangeId));
    }

    [Fact]
    public async Task A_moderator_reads_the_chat_only_with_an_active_grant_for_this_very_exchange()
    {
        var (a, b, exchangeId, c) = await ChatWorldAsync();
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var other = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);

        Assert.False(await CanReadAsync(moderator.User, exchangeId));                  // без доступа
        Assert.False(await HandlerAllowsAsync(moderator.User, exchangeId));

        await GrantAsync(c, moderator.Id, exchangeId);

        Assert.True(await CanReadAsync(moderator.User, exchangeId));                   // с активным доступом
        Assert.True(await HandlerAllowsAsync(moderator.User, exchangeId));
        Assert.False(await CanReadAsync(other.User, exchangeId));                      // чужой доступ не передаётся
        var (_, _, otherExchange, _) = await ChatWorldAsync();
        Assert.False(await CanReadAsync(moderator.User, otherExchange));               // доступ только к этому обмену
        Assert.False(await HandlerAllowsAsync(moderator.User, otherExchange));
    }

    [Fact]
    public async Task A_revoked_grant_closes_the_chat_at_once()
    {
        var (_, _, exchangeId, c) = await ChatWorldAsync();
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        await GrantAsync(c, moderator.Id, exchangeId);
        Assert.True(await CanReadAsync(moderator.User, exchangeId));

        await _factory.QueryAsync(async db =>
        {
            await db.ChatAccessGrants.Where(g => g.ModeratorId == moderator.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, Now));
            return 0;
        });

        Assert.False(await CanReadAsync(moderator.User, exchangeId));
        Assert.False(await HandlerAllowsAsync(moderator.User, exchangeId));
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.SuperAdmin)]
    public async Task An_admin_never_reads_a_chat_even_if_a_grant_were_recorded_for_them(UserRole role)
    {
        var (_, _, exchangeId, c) = await ChatWorldAsync();
        var admin = await ModerationTestData.StaffAsync(_factory, role);
        await GrantAsync(c, admin.Id, exchangeId);

        Assert.False(await CanReadAsync(admin.User, exchangeId));
        Assert.False(await HandlerAllowsAsync(admin.User, exchangeId));
    }

    [Fact]
    public async Task An_unknown_exchange_and_a_broken_subject_are_refused()
    {
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);

        Assert.False(await CanReadAsync(moderator.User, Guid.NewGuid()));
        Assert.False(await AllowedAsync(ModerationTestData.Anonymous(), Guid.NewGuid(), new ChatAccessRequirement()));
        Assert.False(await AllowedAsync(ModerationTestData.PrincipalFor(moderator.User), "не идентификатор", new ChatAccessRequirement()));
    }

    // ---------- журнал аудита ----------

    private Task<AuditLog> LastAuditAsync(Guid actorId) =>
        _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.ActorId == actorId).OrderByDescending(a => a.CreatedAt).FirstAsync());

    [Fact]
    public async Task An_audit_entry_is_written_with_the_actor_the_object_the_details_and_the_time()
    {
        var actor = Guid.NewGuid();
        var target = Guid.NewGuid();
        await _factory.QueryAsync(async db => { db.Users.Add(User.CreateStaff(AuthApi.NewEmail(), "x", UserRole.Moderator)); return 0; });

        await InScopeAsync(_factory.Services, async sp =>
        {
            await sp.GetRequiredService<IAuditLog>().WriteAsync(
                new AuditEntry(actor, AuditActions.ChatRead, "Exchange", target, new { caseId = Guid.Empty, pageSize = 50, cursor = (string?)null }), CancellationToken.None);
            return 0;
        });

        var row = await LastAuditAsync(actor);
        Assert.Equal(AuditActions.ChatRead, row.Action);
        Assert.Equal("Exchange", row.TargetType);
        Assert.Equal(target, row.TargetId);
        Assert.Equal(Now, row.CreatedAt, TimeSpan.FromSeconds(2));
        Assert.Contains("\"pageSize\":50", row.DetailsJson.Replace(" ", string.Empty));   // имена полей в camelCase
        Assert.Contains("caseId", row.DetailsJson);
        Assert.Null(row.IpAddress);   // запроса нет: адреса нет
    }

    [Fact]
    public async Task An_entry_added_inside_a_transaction_follows_its_fate()
    {
        var committed = Guid.NewGuid();
        var rolledBack = Guid.NewGuid();

        await InScopeAsync(_factory.Services, async sp =>
        {
            var uow = sp.GetRequiredService<IUnitOfWork>();
            var audit = sp.GetRequiredService<IAuditLog>();
            var db = sp.GetRequiredService<AppDbContext>();
            await uow.ExecuteAsync(async ct =>
            {
                audit.Add(new AuditEntry(committed, AuditActions.CaseTaken, "Case", Guid.NewGuid()));
                await db.SaveChangesAsync(ct);
                return 0;
            }, CancellationToken.None);
            return 0;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => InScopeAsync(_factory.Services, async sp =>
        {
            var uow = sp.GetRequiredService<IUnitOfWork>();
            var audit = sp.GetRequiredService<IAuditLog>();
            var db = sp.GetRequiredService<AppDbContext>();
            return await uow.ExecuteAsync<int>(async ct =>
            {
                audit.Add(new AuditEntry(rolledBack, AuditActions.CaseTaken, "Case", Guid.NewGuid()));
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException("действие не удалось");
            }, CancellationToken.None);
        }));

        Assert.Equal(1, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.ActorId == committed)));
        Assert.Equal(0, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.ActorId == rolledBack)));
    }

    [Fact]
    public async Task The_audit_log_refuses_update_delete_and_truncate_even_from_the_application_connection()
    {
        var actor = Guid.NewGuid();
        await InScopeAsync(_factory.Services, async sp =>
        {
            await sp.GetRequiredService<IAuditLog>().WriteAsync(new AuditEntry(actor, AuditActions.ChatRead, "Exchange", Guid.NewGuid()), CancellationToken.None);
            return 0;
        });

        async Task<PostgresException> Refused(Func<AppDbContext, Task> action)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => _factory.QueryAsync(async db =>
            {
                await action(db);
                return 0;
            }));
            return Assert.IsType<PostgresException>(error is PostgresException ? error : error.InnerException);
        }

        var update = await Refused(db => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AuditLogs\" SET \"Action\" = 'Forged' WHERE \"ActorId\" = {actor}"));
        var delete = await Refused(db => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AuditLogs\" WHERE \"ActorId\" = {actor}"));
        var truncate = await Refused(db => db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"AuditLogs\""));

        Assert.All(new[] { update, delete, truncate }, e => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, e.SqlState));
        var row = await LastAuditAsync(actor);
        Assert.Equal(AuditActions.ChatRead, row.Action);   // запись цела
    }

    [Fact]
    public async Task Changing_a_tracked_audit_entry_through_the_application_model_is_refused_too()
    {
        var actor = Guid.NewGuid();
        await InScopeAsync(_factory.Services, async sp =>
        {
            await sp.GetRequiredService<IAuditLog>().WriteAsync(new AuditEntry(actor, AuditActions.CaseTaken, "Case", Guid.NewGuid()), CancellationToken.None);
            return 0;
        });

        await Assert.ThrowsAnyAsync<Exception>(() => _factory.QueryAsync(async db =>
        {
            var row = await db.AuditLogs.SingleAsync(a => a.ActorId == actor);
            db.AuditLogs.Remove(row);
            await db.SaveChangesAsync();
            return 0;
        }));

        Assert.Equal(1, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.ActorId == actor)));
    }

    [Fact]
    public void The_request_address_comes_from_the_connection_with_mapped_ipv4_unwrapped_and_is_absent_without_a_request()
    {
        var ipv4 = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ipv4.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.1.2.3");
        var mapped = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        mapped.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("::ffff:10.1.2.3");
        var v6 = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        v6.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("2001:db8::1");

        Assert.Equal("10.1.2.3", new HttpRequestInfo(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = ipv4 }).IpAddress);
        Assert.Equal("10.1.2.3", new HttpRequestInfo(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = mapped }).IpAddress);
        Assert.Equal("2001:db8::1", new HttpRequestInfo(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = v6 }).IpAddress);
        Assert.Null(new HttpRequestInfo(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = null }).IpAddress);   // у HttpContextAccessor общее асинхронное состояние: контекст сбрасываем явно
        Assert.Null(new HttpRequestInfo(new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }).IpAddress);
    }

    // ---------- ограничения таблиц ----------

    private static async Task<PostgresException> PostgresErrorAsync(Func<Task> action)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(action);
        var inner = error as PostgresException ?? error.InnerException as PostgresException;
        return Assert.IsType<PostgresException>(inner);
    }

    private Task AddAsync(params object[] entities) => _factory.QueryAsync(async db =>
    {
        db.AddRange(entities);
        await db.SaveChangesAsync();
        return 0;
    });

    private static Review NewReview(Guid exchangeId, Guid author, Guid target) =>
        Review.Create(exchangeId, author, target, 5, 4, 3, 4, "Хороший преподаватель, всё объяснил понятно.", false);

    [Fact]
    public async Task An_author_writes_one_review_per_exchange_and_the_partner_may_write_theirs()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        await AddAsync(NewReview(exchangeId, a.Id, b.Id));

        var duplicate = await PostgresErrorAsync(() => AddAsync(NewReview(exchangeId, a.Id, b.Id)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        await AddAsync(NewReview(exchangeId, b.Id, a.Id));   // ответный отзыв партнёра — отдельная строка
        Assert.Equal(2, await _factory.QueryAsync(db => db.Reviews.CountAsync(r => r.ExchangeId == exchangeId)));
    }

    [Fact]
    public async Task The_database_itself_refuses_scores_out_of_range_and_a_case_against_oneself()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        var review = NewReview(exchangeId, a.Id, b.Id);
        await AddAsync(review);
        var c = await OpenCaseAsync(exchangeId, a.Id, b.Id);

        var score = await PostgresErrorAsync(() => _factory.QueryAsync(async db =>
        {
            await db.Reviews.Where(r => r.Id == review.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ScoreKnowledge, 6));
            return 0;
        }));
        var self = await PostgresErrorAsync(() => _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(x => x.Id == c.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AgainstUserId, a.Id));
            return 0;
        }));

        Assert.Equal(PostgresErrorCodes.CheckViolation, score.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, self.SqlState);
    }

    [Fact]
    public async Task A_message_attached_to_a_case_cannot_be_deleted_until_it_is_detached()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        var c = await OpenCaseAsync(exchangeId, a.Id, b.Id);
        var messageId = await ModerationTestData.MessageAsync(_factory, exchangeId, b.Id, "Оскорбительное сообщение для обращения");
        await AddAsync(CaseEvidence.Create(c.Id, messageId, Now));

        var refused = await PostgresErrorAsync(() => _factory.QueryAsync(async db =>
        {
            await db.Messages.Where(m => m.Id == messageId).ExecuteDeleteAsync();
            return 0;
        }));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, refused.SqlState);   // очистка чата не потеряет доказательство
        Assert.True(await _factory.QueryAsync(db => db.Messages.AnyAsync(m => m.Id == messageId)));

        await _factory.QueryAsync(async db =>
        {
            await db.CaseEvidence.Where(e => e.CaseId == c.Id).ExecuteDeleteAsync();
            await db.Messages.Where(m => m.Id == messageId).ExecuteDeleteAsync();
            return 0;
        });
        Assert.False(await _factory.QueryAsync(db => db.Messages.AnyAsync(m => m.Id == messageId)));
    }

    [Fact]
    public async Task The_same_message_cannot_be_attached_to_one_case_twice()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        var c = await OpenCaseAsync(exchangeId, a.Id, b.Id);
        var messageId = await ModerationTestData.MessageAsync(_factory, exchangeId, b.Id, "Сообщение-доказательство");
        await AddAsync(CaseEvidence.Create(c.Id, messageId, Now));

        var duplicate = await PostgresErrorAsync(() => AddAsync(CaseEvidence.Create(c.Id, messageId, Now)));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task Deleting_a_case_takes_its_evidence_questions_appeal_and_grants_with_it()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var c = await OpenCaseAsync(exchangeId, a.Id, b.Id);
        var messageId = await ModerationTestData.MessageAsync(_factory, exchangeId, b.Id, "Сообщение");
        await AddAsync(
            CaseEvidence.Create(c.Id, messageId, Now),
            CaseMessage.Create(c.Id, moderator.Id, a.Id, "Уточните, пожалуйста"),
            CaseAppeal.Create(c.Id, a.Id, Text),
            ChatAccessGrant.Create(c.Id, moderator.Id, exchangeId, GrantReason.Case, Now));

        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(x => x.Id == c.Id).ExecuteDeleteAsync();
            return 0;
        });

        Assert.Equal(0, await _factory.QueryAsync(db => db.CaseEvidence.CountAsync(e => e.CaseId == c.Id)));
        Assert.Equal(0, await _factory.QueryAsync(db => db.CaseMessages.CountAsync(m => m.CaseId == c.Id)));
        Assert.Equal(0, await _factory.QueryAsync(db => db.CaseAppeals.CountAsync(x => x.CaseId == c.Id)));
        Assert.Equal(0, await _factory.QueryAsync(db => db.ChatAccessGrants.CountAsync(g => g.CaseId == c.Id)));
        Assert.True(await _factory.QueryAsync(db => db.Messages.AnyAsync(m => m.Id == messageId)));   // сам чат не тронут
    }

    [Fact]
    public async Task A_moderator_has_at_most_one_active_grant_per_case_and_a_new_one_is_possible_after_a_revoke()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var c = await OpenCaseAsync(exchangeId, a.Id, b.Id);
        var first = ChatAccessGrant.Create(c.Id, moderator.Id, exchangeId, GrantReason.Case, Now);
        await AddAsync(first);

        var second = await PostgresErrorAsync(() => AddAsync(ChatAccessGrant.Create(c.Id, moderator.Id, exchangeId, GrantReason.Case, Now)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, second.SqlState);

        await _factory.QueryAsync(async db =>
        {
            await db.ChatAccessGrants.Where(g => g.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, Now));
            return 0;
        });
        await AddAsync(ChatAccessGrant.Create(c.Id, moderator.Id, exchangeId, GrantReason.AppealReview, Now));   // после отзыва можно (повторная проверка)
        Assert.Equal(2, await _factory.QueryAsync(db => db.ChatAccessGrants.CountAsync(g => g.CaseId == c.Id)));
    }

    [Fact]
    public async Task A_case_has_one_appeal()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        var c = await OpenCaseAsync(exchangeId, a.Id, b.Id);
        await AddAsync(CaseAppeal.Create(c.Id, a.Id, Text));

        var duplicate = await PostgresErrorAsync(() => AddAsync(CaseAppeal.Create(c.Id, b.Id, Text)));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task One_personal_account_belongs_to_at_most_one_staff_account()
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var personal = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        await ModerationTestData.StaffAsync(_factory, UserRole.Moderator, personal);

        var second = await PostgresErrorAsync(() => ModerationTestData.StaffAsync(_factory, UserRole.Admin, personal));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, second.SqlState);
    }

    // ---------- оптимистичная блокировка ----------

    [Fact]
    public async Task Two_moderators_cannot_take_the_same_case_the_second_save_is_a_concurrency_conflict()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        var c = await OpenCaseAsync(exchangeId, a.Id, b.Id);
        var first = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var second = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);

        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<AppDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var case1 = await db1.Cases.SingleAsync(x => x.Id == c.Id);
        var case2 = await db2.Cases.SingleAsync(x => x.Id == c.Id);

        case1.Take(first.Id);
        case2.Take(second.Id);
        await db1.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        var stored = await _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id));
        Assert.Equal(first.Id, stored.ModeratorId);
        Assert.Equal(CaseStatus.InProgress, stored.Status);
    }

    [Fact]
    public async Task Two_admins_cannot_decide_the_same_ban_request()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        var c = await OpenCaseAsync(exchangeId, a.Id, b.Id);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var admin1 = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var admin2 = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var request = BanRequest.ByModerator(b.Id, c.Id, moderator.Id, Text);
        await AddAsync(request);

        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<AppDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var r1 = await db1.BanRequests.SingleAsync(x => x.Id == request.Id);
        var r2 = await db2.BanRequests.SingleAsync(x => x.Id == request.Id);
        r1.Approve(admin1.Id, Now);
        r2.Reject(admin2.Id, Now);
        await db1.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        Assert.Equal(BanRequestStatus.Approved, (await _factory.QueryAsync(db => db.BanRequests.AsNoTracking().SingleAsync(x => x.Id == request.Id))).Status);
    }

    [Fact]
    public async Task A_review_published_and_frozen_at_the_same_time_cannot_both_win()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        var review = NewReview(exchangeId, a.Id, b.Id);
        review.Publish(Now);
        await AddAsync(review);

        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<AppDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var r1 = await db1.Reviews.SingleAsync(x => x.Id == review.Id);
        var r2 = await db2.Reviews.SingleAsync(x => x.Id == review.Id);
        r1.Freeze();
        r2.Hide();
        await db1.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        Assert.Equal(ReviewStatus.Frozen, (await _factory.QueryAsync(db => db.Reviews.AsNoTracking().SingleAsync(x => x.Id == review.Id))).Status);
    }

    // ---------- хранение ----------

    [Fact]
    public async Task Enumerations_are_stored_as_readable_text_and_a_case_survives_a_round_trip()
    {
        var (_, a, b, exchangeId) = await ModerationTestData.ExchangeAsync(_factory, _client);
        var c = Case.Open(CaseType.NoShowAbuse, CaseCategory.BadFaithLessons, exchangeId, null, a.Id, b.Id, Text, minorInvolved: true, false, Now);
        await AddAsync(c);

        var raw = await _factory.QueryAsync(db => db.Database
            .SqlQuery<string>($"SELECT \"Type\" || '/' || \"Category\" || '/' || \"Priority\" || '/' || \"Status\" AS \"Value\" FROM \"Cases\" WHERE \"Id\" = {c.Id}").SingleAsync());
        var loaded = await _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(x => x.Id == c.Id));

        Assert.Equal("NoShowAbuse/BadFaithLessons/High/Open", raw);
        Assert.Equal(CasePriority.High, loaded.Priority);
        Assert.Equal(c.DueAt, loaded.DueAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(Text, loaded.Description);
        Assert.Null(loaded.Decision);
    }
}
