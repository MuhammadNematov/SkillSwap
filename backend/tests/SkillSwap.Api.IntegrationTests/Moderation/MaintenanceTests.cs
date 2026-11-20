using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>
/// Хранение и очистка (раздел 10.3 ТЗ): очистка чатов через 90 дней с защитой сообщений-доказательств, страховочная сверка обращений
/// (возврат из ожидания, закрытие, «зависшие» доступы, просроченные сроки реакции), снятие удержания данных и расписание повторяющихся задач.
/// </summary>
[Collection(ApiCollection.Name)]
public class MaintenanceTests
{
    private const string Complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";
    private const string Reason = "Переписка подтверждает жалобу: оскорбления были, договорённости сорваны.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public MaintenanceTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private InlineBackgroundJobs Jobs => (InlineBackgroundJobs)_factory.Services.GetRequiredService<IBackgroundJobs>();

    private async Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<Guid> FileAsync(MatchPerson who, Guid exchangeId, object? messageIds = null, string type = "Conduct", string category = "Insults")
    {
        var response = await SendAsync(who.Token, HttpMethod.Post, "/cases", new { type, category, exchangeId, description = Complaint, messageIds });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private Task CloseExchangeAsync(Guid exchangeId, int daysAgo) => _factory.QueryAsync(async db =>
    {
        await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, ExchangeStatus.Completed).SetProperty(e => e.ClosedAt, Now.AddDays(-daysAgo)));
        return 0;
    });

    private async Task RunPurgeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ChatCleanupJobs>().PurgeAsync();
    }

    private async Task RunSweepAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CaseMaintenanceJobs>().SweepAsync();
    }

    private Task<int> MessagesAsync(Guid exchangeId) => _factory.QueryAsync(db => db.Messages.CountAsync(m => m.ExchangeId == exchangeId));

    private Task<Exchange> ExchangeRowAsync(Guid id) => _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == id));

    private Task<Case> CaseAsync(Guid id) => _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task<int> AuditCountAsync(string action, Guid? target = null) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == action && (target == null || a.TargetId == target)));

    /// <summary>Вложение с настоящим файлом в хранилище: строка в базе, сообщение и объект.</summary>
    private async Task<(Guid MessageId, Guid AttachmentId, string Key)> AttachmentMessageAsync(Guid exchangeId, Guid senderId)
    {
        var attachment = Attachment.Create(senderId, exchangeId, AttachmentKind.File, "notes.txt", "text/plain", 5, null);
        var message = Message.CreateAttachment(exchangeId, senderId, MessageType.File, attachment.Id);
        await _factory.QueryAsync(async db =>
        {
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync();
            db.Messages.Add(message);
            await db.SaveChangesAsync();
            return 0;
        });
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IFileStorage>().PutAsync(attachment.StorageKey, new MemoryStream(Encoding.UTF8.GetBytes("hello")), "text/plain", CancellationToken.None);
        return (message.Id, attachment.Id, attachment.StorageKey);
    }

    private async Task<bool> FileExistsAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IFileStorage>().GetAsync(key, CancellationToken.None);
        stored?.Content.Dispose();
        return stored is not null;
    }

    // ======================= очистка чатов =======================

    [Fact]
    public async Task A_chat_of_an_exchange_closed_over_90_days_ago_is_purged_with_its_versions_and_files()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.A.Id, "Старое сообщение");
        var edited = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Сообщение с правкой");
        await _factory.QueryAsync(async db =>
        {
            db.MessageRevisions.Add(MessageRevision.Create(edited, "шифр", Now));
            await db.SaveChangesAsync();
            return 0;
        });
        var file = await AttachmentMessageAsync(x.ExchangeId, x.A.Id);
        await CloseExchangeAsync(x.ExchangeId, 91);

        await RunPurgeAsync();

        Assert.Equal(0, await MessagesAsync(x.ExchangeId));
        Assert.Equal(0, await _factory.QueryAsync(db => db.MessageRevisions.CountAsync(r => r.MessageId == edited)));
        Assert.Equal(0, await _factory.QueryAsync(db => db.Attachments.CountAsync(a => a.Id == file.AttachmentId)));
        Assert.False(await FileExistsAsync(file.Key));
        Assert.NotNull((await ExchangeRowAsync(x.ExchangeId)).ChatPurgedAt);
        Assert.Equal(1, await AuditCountAsync("ChatPurged", x.ExchangeId));

        await RunPurgeAsync();   // повтор ничего не делает
        Assert.Equal(1, await AuditCountAsync("ChatPurged", x.ExchangeId));
    }

    [Fact]
    public async Task A_younger_closed_exchange_and_an_active_one_are_not_touched()
    {
        var young = await ModerationTestData.ExchangeAsync(_factory, _client);
        var active = await ModerationTestData.ExchangeAsync(_factory, _client);
        await ModerationTestData.MessageAsync(_factory, young.ExchangeId, young.A.Id, "Недавний обмен");
        await ModerationTestData.MessageAsync(_factory, active.ExchangeId, active.A.Id, "Идущий обмен");
        await CloseExchangeAsync(young.ExchangeId, 89);

        await RunPurgeAsync();

        Assert.Equal(1, await MessagesAsync(young.ExchangeId));
        Assert.Equal(1, await MessagesAsync(active.ExchangeId));
        Assert.Null((await ExchangeRowAsync(young.ExchangeId)).ChatPurgedAt);
    }

    [Theory]
    [InlineData(CaseStatus.Open)]
    [InlineData(CaseStatus.InProgress)]
    [InlineData(CaseStatus.Resolved)]
    [InlineData(CaseStatus.Appealed)]
    public async Task An_open_case_or_appeal_keeps_the_whole_chat(CaseStatus status)
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.A.Id, "Сообщение под обращением");
        var caseId = await FileAsync(x.A, x.ExchangeId);
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, status));
            return 0;
        });
        await CloseExchangeAsync(x.ExchangeId, 120);

        await RunPurgeAsync();

        Assert.Equal(1, await MessagesAsync(x.ExchangeId));
    }

    [Fact]
    public async Task Once_the_case_is_closed_the_chat_is_purged_but_the_evidence_stays_for_a_year()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.A.Id, "Обычное сообщение");
        var evidence = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Грубость как доказательство");
        var evidenceFile = await AttachmentMessageAsync(x.ExchangeId, x.B.Id);
        var other = await AttachmentMessageAsync(x.ExchangeId, x.A.Id);
        var caseId = await FileAsync(x.A, x.ExchangeId, new[] { evidence, evidenceFile.MessageId });
        await CloseExchangeAsync(x.ExchangeId, 120);
        await RunPurgeAsync();
        Assert.Equal(4, await MessagesAsync(x.ExchangeId));   // открытое обращение: ничего не удалено

        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CaseStatus.Closed).SetProperty(c => c.ClosedAt, Now.AddDays(-10)));
            return 0;
        });
        await RunPurgeAsync();

        Assert.Equal(2, await MessagesAsync(x.ExchangeId));   // остались только доказательства
        Assert.Equal(2, await _factory.QueryAsync(db => db.Messages.CountAsync(m => m.ExchangeId == x.ExchangeId && (m.Id == evidence || m.Id == evidenceFile.MessageId))));
        Assert.True(await FileExistsAsync(evidenceFile.Key));   // файл-доказательство цел
        Assert.False(await FileExistsAsync(other.Key));
        Assert.Null((await ExchangeRowAsync(x.ExchangeId)).ChatPurgedAt);   // сообщения ещё остались: вернёмся к обмену позже
        Assert.Equal(1, await _factory.QueryAsync(db => db.Attachments.CountAsync(a => a.Id == evidenceFile.AttachmentId)));
    }

    [Fact]
    public async Task The_evidence_is_released_a_year_after_the_case_closed_and_then_purged()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var evidence = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Доказательство");
        var file = await AttachmentMessageAsync(x.ExchangeId, x.B.Id);
        var caseId = await FileAsync(x.A, x.ExchangeId, new[] { evidence, file.MessageId });
        await CloseExchangeAsync(x.ExchangeId, 400);
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CaseStatus.Closed).SetProperty(c => c.ClosedAt, Now.AddDays(-364)));
            return 0;
        });
        await RunPurgeAsync();
        Assert.Equal(2, await MessagesAsync(x.ExchangeId));   // год ещё не прошёл

        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ClosedAt, Now.AddDays(-366)));
            return 0;
        });
        await RunPurgeAsync();

        Assert.Equal(0, await MessagesAsync(x.ExchangeId));
        Assert.Equal(0, await _factory.QueryAsync(db => db.CaseEvidence.CountAsync(e => e.CaseId == caseId)));
        Assert.False(await FileExistsAsync(file.Key));
        Assert.NotNull((await ExchangeRowAsync(x.ExchangeId)).ChatPurgedAt);
        Assert.True(await AuditCountAsync("EvidenceReleased") >= 1);
        Assert.NotNull(await _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(c => c.Id == caseId)));   // само обращение остаётся
    }

    [Fact]
    public async Task The_evidence_of_a_withdrawn_case_is_not_protected()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var message = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Доказательство отозванного обращения");
        var caseId = await FileAsync(x.A, x.ExchangeId, new[] { message });
        await SendAsync(x.A.Token, HttpMethod.Post, $"/cases/{caseId}/withdraw");
        await CloseExchangeAsync(x.ExchangeId, 100);

        await RunPurgeAsync();

        Assert.Equal(0, await MessagesAsync(x.ExchangeId));
        Assert.NotNull((await ExchangeRowAsync(x.ExchangeId)).ChatPurgedAt);
    }

    [Fact]
    public async Task The_database_itself_refuses_to_delete_a_message_attached_as_evidence()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var message = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Защищённое сообщение");
        await FileAsync(x.A, x.ExchangeId, new[] { message });

        var attempt = await Record.ExceptionAsync(() => _factory.QueryAsync(async db => await db.Messages.Where(m => m.Id == message).ExecuteDeleteAsync()));

        Assert.NotNull(attempt);
        Assert.Equal(1, await MessagesAsync(x.ExchangeId));
    }

    // ======================= сверка обращений =======================

    private async Task<(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid CaseId, StaffPerson Moderator)> TakenAsync()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var caseId = await FileAsync(x.A, x.ExchangeId);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/take")).StatusCode);
        return (x.A, x.B, x.ExchangeId, caseId, moderator);
    }

    [Fact]
    public async Task The_sweep_returns_a_case_to_work_when_the_explanation_deadline_passed_and_the_job_did_not_run()
    {
        var s = await TakenAsync();
        await SendAsync(s.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{s.CaseId}/questions", new { recipient = "Both", text = "Опишите, что произошло." });
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == s.CaseId).ExecuteUpdateAsync(x => x.SetProperty(c => c.ResponseDeadlineAt, Now.AddHours(-2)));
            return 0;
        });

        await RunSweepAsync();

        Assert.Equal(CaseStatus.InProgress, (await CaseAsync(s.CaseId)).Status);
        var notes = (await JsonAsync(await SendAsync(s.Moderator.Token, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items").EnumerateArray();
        Assert.Single(notes, n => n.GetProperty("type").GetString() == "CaseReturnedToWork");
    }

    [Fact]
    public async Task The_sweep_closes_a_resolved_case_after_seven_days_and_leaves_a_fresh_or_appealed_one()
    {
        var closed = await TakenAsync();
        await SendAsync(closed.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{closed.CaseId}/resolve", new { decision = "NoViolation", alsoWarned = false, reason = Reason });
        var fresh = await TakenAsync();
        await SendAsync(fresh.Moderator.Token, HttpMethod.Post, $"/moderation/cases/{fresh.CaseId}/resolve", new { decision = "NoViolation", alsoWarned = false, reason = Reason });
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == closed.CaseId).ExecuteUpdateAsync(x => x.SetProperty(c => c.ResolvedAt, Now.AddDays(-8)));
            await db.Cases.Where(c => c.Id == fresh.CaseId).ExecuteUpdateAsync(x => x.SetProperty(c => c.ResolvedAt, Now.AddDays(-6)));
            return 0;
        });

        await RunSweepAsync();

        Assert.Equal(CaseStatus.Closed, (await CaseAsync(closed.CaseId)).Status);
        Assert.Equal(CaseStatus.Resolved, (await CaseAsync(fresh.CaseId)).Status);
        Assert.Equal(1, await AuditCountAsync("CaseClosed", closed.CaseId));
    }

    [Fact]
    public async Task The_sweep_revokes_chat_access_that_outlived_its_case_and_keeps_the_valid_ones()
    {
        var valid = await TakenAsync();    // доступ действует: обращение в работе
        var resolved = await TakenAsync();
        var appealReviewValid = await TakenAsync();
        var appealReviewStale = await TakenAsync();
        var extra = await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == resolved.CaseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CaseStatus.Closed));   // доступ остался (сбой отзыва)
            await db.Cases.Where(c => c.Id == appealReviewValid.CaseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CaseStatus.AppealReview));
            await db.Cases.Where(c => c.Id == appealReviewStale.CaseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CaseStatus.Appealed));
            var reviewerA = User.CreateStaff(AuthApi.NewEmail(), "Проверяющий 1", UserRole.Moderator);
            var reviewerB = User.CreateStaff(AuthApi.NewEmail(), "Проверяющий 2", UserRole.Moderator);
            db.Users.AddRange(reviewerA, reviewerB);
            await db.SaveChangesAsync();
            db.ChatAccessGrants.Add(ChatAccessGrant.Create(appealReviewValid.CaseId, reviewerA.Id, appealReviewValid.ExchangeId, GrantReason.AppealReview, Now));
            db.ChatAccessGrants.Add(ChatAccessGrant.Create(appealReviewStale.CaseId, reviewerB.Id, appealReviewStale.ExchangeId, GrantReason.AppealReview, Now));
            await db.SaveChangesAsync();
            return (reviewerA.Id, reviewerB.Id);
        });

        await RunSweepAsync();

        var grants = await _factory.QueryAsync(db => db.ChatAccessGrants.AsNoTracking().ToListAsync());
        ChatAccessGrant Of(Guid caseId, GrantReason reason) => grants.Single(g => g.CaseId == caseId && g.Reason == reason);
        Assert.Null(Of(valid.CaseId, GrantReason.Case).RevokedAt);                    // обращение в работе
        Assert.NotNull(Of(resolved.CaseId, GrantReason.Case).RevokedAt);               // обращение закрыто
        Assert.Null(Of(appealReviewValid.CaseId, GrantReason.AppealReview).RevokedAt);   // идёт повторная проверка
        Assert.NotNull(Of(appealReviewStale.CaseId, GrantReason.AppealReview).RevokedAt);   // проверка закончилась
        Assert.NotNull(Of(appealReviewStale.CaseId, GrantReason.Case).RevokedAt);          // и доступ первого модератора по Appealed тоже лишний
        Assert.NotNull(Of(appealReviewValid.CaseId, GrantReason.Case).RevokedAt);          // в AppealReview доступа первого модератора быть не должно
        Assert.Equal(1, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == "ChatAccessRevoked" && a.TargetId == resolved.ExchangeId && a.ActorId == null)));
        Assert.NotEqual(extra.Item1, extra.Item2);

        // Повторный проход ничего не добавляет.
        var audited = await AuditCountAsync("ChatAccessRevoked");
        await RunSweepAsync();
        Assert.Equal(audited, await AuditCountAsync("ChatAccessRevoked"));
    }

    [Fact]
    public async Task The_sweep_marks_an_overdue_case_once_and_tells_the_admins()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var overdue = await FileAsync(x.A, x.ExchangeId);
        var fresh = await FileAsync(x.A, x.ExchangeId, null, "NoShowAbuse", "SystematicEvasion");
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == overdue).ExecuteUpdateAsync(s => s.SetProperty(c => c.DueAt, Now.AddHours(-1)));
            return 0;
        });

        await RunSweepAsync();
        await RunSweepAsync();

        Assert.NotNull((await CaseAsync(overdue)).OverdueMarkedAt);
        Assert.Null((await CaseAsync(fresh)).OverdueMarkedAt);
        Assert.Equal(1, await AuditCountAsync("CaseOverdue", overdue));
        var notes = (await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items").EnumerateArray()
            .Where(n => n.GetProperty("type").GetString() == "CaseOverdue" && n.GetProperty("payload").GetProperty("caseId").GetGuid() == overdue).ToList();
        Assert.Single(notes);
        var card = await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/cases/{overdue}"));
        Assert.True(card.GetProperty("case").GetProperty("isOverdue").GetBoolean());
        Assert.False((await JsonAsync(await SendAsync(admin.Token, HttpMethod.Get, $"/admin/cases/{fresh}"))).GetProperty("case").GetProperty("isOverdue").GetBoolean());
    }

    [Fact]
    public async Task A_case_that_was_taken_in_time_is_never_marked_overdue()
    {
        var s = await TakenAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == s.CaseId).ExecuteUpdateAsync(x => x.SetProperty(c => c.DueAt, Now.AddHours(-5)));
            return 0;
        });

        await RunSweepAsync();

        Assert.Null((await CaseAsync(s.CaseId)).OverdueMarkedAt);
        Assert.Equal(0, await AuditCountAsync("CaseOverdue", s.CaseId));
    }

    // ======================= удержание данных =======================

    private async Task<(MatchPerson A, MatchPerson B, Guid CaseId)> CaseWithDeletedPartyAsync()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var caseId = await FileAsync(x.A, x.ExchangeId);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == x.B.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Deleted).SetProperty(u => u.ErasurePending, true).SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N")));   // у тестового пользователя нет штампа безопасности, а Identity без него не сохраняет
            return 0;
        });
        return (x.A, x.B, caseId);
    }

    private Task<User> UserAsync(Guid id) => _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    [Fact]
    public async Task The_sweep_wipes_the_held_data_of_a_deleted_party_only_after_the_case_is_closed()
    {
        var s = await CaseWithDeletedPartyAsync();
        var before = await UserAsync(s.B.Id);
        Assert.True(before.ErasurePending);

        await RunSweepAsync();
        Assert.True((await UserAsync(s.B.Id)).ErasurePending);   // обращение ещё открыто: данные удержаны

        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == s.CaseId).ExecuteUpdateAsync(x => x.SetProperty(c => c.Status, CaseStatus.Closed));
            return 0;
        });
        await RunSweepAsync();

        var after = await UserAsync(s.B.Id);
        Assert.False(after.ErasurePending);
        Assert.Equal(User.DeletedDisplayName, after.Name);
    }

    [Fact]
    public async Task Closing_a_case_by_the_seven_day_job_releases_the_data_at_once()
    {
        var s = await CaseWithDeletedPartyAsync();
        await _factory.QueryAsync(async db =>
        {
            var found = await db.Cases.SingleAsync(c => c.Id == s.CaseId);
            var moderator = User.CreateStaff(AuthApi.NewEmail(), "Модератор", UserRole.Moderator);
            db.Users.Add(moderator);
            await db.SaveChangesAsync();
            found.Take(moderator.Id);
            found.Resolve(moderator.Id, CaseDecision.NoViolation, false, Reason, Now.AddDays(-8));
            await db.SaveChangesAsync();
            return 0;
        });
        using var scope = _factory.Services.CreateScope();

        await scope.ServiceProvider.GetRequiredService<CaseJobs>().CloseIfDueAsync(s.CaseId);

        Assert.Equal(CaseStatus.Closed, (await CaseAsync(s.CaseId)).Status);
        Assert.False((await UserAsync(s.B.Id)).ErasurePending);
    }

    [Fact]
    public async Task Withdrawing_a_case_releases_the_data_of_the_deleted_party()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var caseId = await FileAsync(x.A, x.ExchangeId);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == x.B.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Deleted).SetProperty(u => u.ErasurePending, true).SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N")));   // у тестового пользователя нет штампа безопасности, а Identity без него не сохраняет
            return 0;
        });

        var response = await SendAsync(x.A.Token, HttpMethod.Post, $"/cases/{caseId}/withdraw");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await UserAsync(x.B.Id)).ErasurePending);
    }

    // ======================= расписание =======================

    [Fact]
    public async Task The_recurring_jobs_are_registered_with_their_schedules_and_run_without_failures()
    {
        RecurringCatalog.Register(Jobs);

        var registered = Jobs.RecurringJobs;
        Assert.Equal(RecurringCatalog.SweepCron, registered[RecurringCatalog.SweepId]);
        Assert.Equal(RecurringCatalog.ChatCleanupCron, registered[RecurringCatalog.ChatCleanupId]);

        var failures = Jobs.Failures.Count;
        await Jobs.RunRecurringAsync(RecurringCatalog.SweepId);
        await Jobs.RunRecurringAsync(RecurringCatalog.ChatCleanupId);
        Assert.Equal(failures, Jobs.Failures.Count);

        RecurringCatalog.Register(Jobs);   // повторная регистрация обновляет, а не дублирует
        Assert.Equal(registered.Count, Jobs.RecurringJobs.Count);
    }
}
