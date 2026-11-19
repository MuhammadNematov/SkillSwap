using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Moderation;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>Пояснения по обращению (раздел 8.2 ТЗ): вопрос модератора, ответы участников, срок 48 часов и возврат в работу.</summary>
[Collection(ApiCollection.Name)]
public class ExplanationsTests
{
    private const string Complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ExplanationsTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private SkillSwap.Infrastructure.Jobs.InlineBackgroundJobs Jobs => (SkillSwap.Infrastructure.Jobs.InlineBackgroundJobs)_factory.Services.GetRequiredService<SkillSwap.Application.Abstractions.IBackgroundJobs>();

    private async Task<HttpResponseMessage> SendAsync(string? token, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    /// <summary>Обращение, поданное A на B и взятое модератором.</summary>
    private async Task<(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid CaseId, StaffPerson Moderator, MatchTestWorld World)> TakenAsync()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var created = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = Complaint });
        var caseId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/take")).StatusCode);
        return (x.A, x.B, x.ExchangeId, caseId, moderator, x.World);
    }

    private Task<HttpResponseMessage> AskAsync(StaffPerson moderator, Guid caseId, string recipient = "Both", string? text = "Опишите, пожалуйста, что произошло на уроке.") =>
        SendAsync(moderator.Token, HttpMethod.Post, $"/moderation/cases/{caseId}/questions", new { recipient, text });

    private Task<HttpResponseMessage> AnswerAsync(MatchPerson who, Guid caseId, Guid questionId, string? text = "Вот что произошло на самом деле.") =>
        SendAsync(who.Token, HttpMethod.Post, $"/cases/{caseId}/explanations", new { questionId, text });

    private async Task<List<JsonElement>> NotificationsAsync(MatchPerson who, string type)
    {
        var items = (await JsonAsync(await SendAsync(who.Token, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return items.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    private async Task<Guid> QuestionForAsync(MatchPerson who, Guid caseId) =>
        (await JsonAsync(await SendAsync(who.Token, HttpMethod.Get, $"/cases/{caseId}/explanations"))).GetProperty("items").EnumerateArray()
        .Single(i => i.GetProperty("kind").GetString() == "Question").GetProperty("id").GetGuid();

    private Task<Case> CaseAsync(Guid id) => _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task ExpireAsync(Guid caseId) => _factory.QueryAsync(async db =>
    {
        await db.Cases.Where(c => c.Id == caseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponseDeadlineAt, Now.AddHours(-1)));
        return 0;
    });

    private async Task RunJobAsync(Guid caseId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CaseJobs>().ExplanationDueAsync(caseId);
    }

    private Task<int> AuditCountAsync(string action, Guid caseId) =>
        _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == action && a.TargetId == caseId));

    // ---------- вопрос ----------

    [Fact]
    public async Task A_question_to_both_moves_the_case_to_waiting_with_a_48_hour_deadline_and_notifies_each_side_once()
    {
        var x = await TakenAsync();

        var response = await AskAsync(x.Moderator, x.CaseId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var thread = await JsonAsync(response);
        Assert.Equal("WaitingResponse", thread.GetProperty("status").GetString());
        Assert.Equal(2, thread.GetProperty("items").GetArrayLength());
        var stored = await CaseAsync(x.CaseId);
        Assert.Equal(CaseStatus.WaitingResponse, stored.Status);
        Assert.InRange((stored.ResponseDeadlineAt!.Value - Now).TotalHours, 47.9, 48.1);
        Assert.Equal(1, await AuditCountAsync("ExplanationRequested", x.CaseId));

        foreach (var who in new[] { x.A, x.B })
        {
            var note = Assert.Single(await NotificationsAsync(who, "CaseExplanationRequested"));
            Assert.Equal(x.CaseId, note.GetProperty("payload").GetProperty("caseId").GetGuid());
            var dto = await JsonAsync(await SendAsync(who.Token, HttpMethod.Get, $"/cases/{x.CaseId}"));
            Assert.Equal(1, dto.GetProperty("pendingQuestions").GetInt32());
            Assert.Equal("WaitingResponse", dto.GetProperty("status").GetString());
            Assert.NotEqual(JsonValueKind.Null, dto.GetProperty("responseDeadlineAt").ValueKind);
        }

        var card = await JsonAsync(await SendAsync(x.Moderator.Token, HttpMethod.Get, $"/moderation/cases/{x.CaseId}"));
        Assert.Equal(2, card.GetProperty("pendingQuestions").GetInt32());
        // Доступ к чату на время ожидания сохраняется.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(x.Moderator.Token, HttpMethod.Get, $"/moderation/cases/{x.CaseId}/chat")).StatusCode);
    }

    [Fact]
    public async Task A_question_to_one_side_reaches_only_that_side()
    {
        var x = await TakenAsync();

        var response = await AskAsync(x.Moderator, x.CaseId, "Against");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await NotificationsAsync(x.B, "CaseExplanationRequested"));
        Assert.Empty(await NotificationsAsync(x.A, "CaseExplanationRequested"));
        var forA = await JsonAsync(await SendAsync(x.A.Token, HttpMethod.Get, $"/cases/{x.CaseId}/explanations"));
        Assert.Equal(0, forA.GetProperty("items").GetArrayLength());   // чужие вопросы не видны
    }

    [Fact]
    public async Task Only_the_moderator_who_took_the_case_can_ask()
    {
        var x = await TakenAsync();
        var other = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var admin = await ModerationTestData.StaffAsync(_factory, UserRole.Admin);
        var opened = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type = "NoShowAbuse", category = "SystematicEvasion", exchangeId = x.ExchangeId, description = Complaint });
        var untaken = (await JsonAsync(opened)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await AskAsync(other, x.CaseId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AskAsync(x.Moderator, untaken)).StatusCode);   // не взято
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(admin.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/questions", new { recipient = "Both", text = "Вопрос" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(x.B.Token, HttpMethod.Post, $"/moderation/cases/{x.CaseId}/questions", new { recipient = "Both", text = "Вопрос" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other.Token, HttpMethod.Get, $"/moderation/cases/{x.CaseId}/explanations")).StatusCode);
        Assert.Equal(CaseStatus.InProgress, (await CaseAsync(x.CaseId)).Status);
    }

    [Theory]
    [InlineData(null, "case_message_empty")]
    [InlineData("   ", "case_message_empty")]
    public async Task An_empty_question_is_rejected_and_changes_nothing(string? text, string code)
    {
        var x = await TakenAsync();

        var response = await AskAsync(x.Moderator, x.CaseId, text: text);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
        Assert.Equal(CaseStatus.InProgress, (await CaseAsync(x.CaseId)).Status);
        Assert.Empty(await NotificationsAsync(x.A, "CaseExplanationRequested"));
    }

    [Fact]
    public async Task A_second_request_while_waiting_is_rejected()
    {
        var x = await TakenAsync();
        await AskAsync(x.Moderator, x.CaseId);

        var again = await AskAsync(x.Moderator, x.CaseId);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("explanation_already_requested", await CodeAsync(again));
        Assert.Single(await NotificationsAsync(x.A, "CaseExplanationRequested"));
    }

    [Fact]
    public async Task No_more_than_ten_questions_can_be_asked_in_one_case()
    {
        var x = await TakenAsync();
        var moderatorId = x.Moderator.Id;
        await _factory.QueryAsync(async db =>
        {
            for (var i = 0; i < 9; i++)
            {
                db.CaseMessages.Add(CaseMessage.Create(x.CaseId, moderatorId, x.A.Id, "Раньше заданный вопрос"));
            }

            await db.SaveChangesAsync();
            return 0;
        });

        var response = await AskAsync(x.Moderator, x.CaseId);   // 9 + 2 больше 10

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("too_many_questions", await CodeAsync(response));
        Assert.Equal(CaseStatus.InProgress, (await CaseAsync(x.CaseId)).Status);
    }

    // ---------- ответы ----------

    [Fact]
    public async Task The_case_returns_to_work_when_everyone_has_answered_and_not_before()
    {
        var x = await TakenAsync();
        await AskAsync(x.Moderator, x.CaseId);
        var qa = await QuestionForAsync(x.A, x.CaseId);
        var qb = await QuestionForAsync(x.B, x.CaseId);

        var first = await AnswerAsync(x.A, x.CaseId, qa);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(CaseStatus.WaitingResponse, (await CaseAsync(x.CaseId)).Status);   // B ещё не ответил

        var second = await AnswerAsync(x.B, x.CaseId, qb);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var stored = await CaseAsync(x.CaseId);
        Assert.Equal(CaseStatus.InProgress, stored.Status);
        Assert.Null(stored.ResponseDeadlineAt);

        var mine = await JsonAsync(second);
        Assert.Equal(2, mine.GetProperty("items").GetArrayLength());   // свой вопрос и свой ответ
        Assert.True(mine.GetProperty("items")[0].GetProperty("answered").GetBoolean());
        var all = await JsonAsync(await SendAsync(x.Moderator.Token, HttpMethod.Get, $"/moderation/cases/{x.CaseId}/explanations"));
        Assert.Equal(4, all.GetProperty("items").GetArrayLength());
        Assert.Equal(2, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.Action == "ExplanationAnswered" && a.TargetId == x.CaseId)));
    }

    [Fact]
    public async Task Answering_someone_elses_question_or_twice_or_with_empty_text_is_rejected()
    {
        var x = await TakenAsync();
        await AskAsync(x.Moderator, x.CaseId);
        var qa = await QuestionForAsync(x.A, x.CaseId);
        var qb = await QuestionForAsync(x.B, x.CaseId);

        var foreign = await AnswerAsync(x.A, x.CaseId, qb);
        var unknown = await AnswerAsync(x.A, x.CaseId, Guid.NewGuid());
        var empty = await AnswerAsync(x.A, x.CaseId, qa, text: "  ");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("question_not_found", await CodeAsync(foreign));
        Assert.Equal("question_not_found", await CodeAsync(unknown));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await AnswerAsync(x.A, x.CaseId, qa)).StatusCode);
        var twice = await AnswerAsync(x.A, x.CaseId, qa);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("question_already_answered", await CodeAsync(twice));
    }

    [Fact]
    public async Task Answering_without_a_request_or_after_the_deadline_is_rejected()
    {
        var x = await TakenAsync();
        var idle = await AnswerAsync(x.A, x.CaseId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Conflict, idle.StatusCode);
        Assert.Equal("explanation_not_requested", await CodeAsync(idle));

        await AskAsync(x.Moderator, x.CaseId);
        var qa = await QuestionForAsync(x.A, x.CaseId);
        await ExpireAsync(x.CaseId);

        var late = await AnswerAsync(x.A, x.CaseId, qa);

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("explanation_window_closed", await CodeAsync(late));
    }

    [Fact]
    public async Task A_stranger_cannot_answer_or_read_the_thread()
    {
        var x = await TakenAsync();
        await AskAsync(x.Moderator, x.CaseId);
        var qa = await QuestionForAsync(x.A, x.CaseId);
        var stranger = await x.World.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));

        Assert.Equal(HttpStatusCode.NotFound, (await AnswerAsync(stranger, x.CaseId, qa)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(stranger.Token, HttpMethod.Get, $"/cases/{x.CaseId}/explanations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(x.Moderator.Token, HttpMethod.Get, $"/cases/{x.CaseId}/explanations")).StatusCode);   // служебным обычные маршруты закрыты
    }

    [Fact]
    public async Task The_accused_cannot_read_the_thread_of_a_case_that_is_not_taken_yet()
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var created = await SendAsync(x.A.Token, HttpMethod.Post, "/cases", new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = Complaint });
        var caseId = (await JsonAsync(created)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(x.B.Token, HttpMethod.Get, $"/cases/{caseId}/explanations")).StatusCode);
    }

    // ---------- срок 48 часов ----------

    [Fact]
    public async Task After_48_hours_the_job_returns_the_case_to_work_with_what_is_there()
    {
        var x = await TakenAsync();
        await AskAsync(x.Moderator, x.CaseId);
        await AnswerAsync(x.A, x.CaseId, await QuestionForAsync(x.A, x.CaseId));   // B промолчал
        await ExpireAsync(x.CaseId);

        await RunJobAsync(x.CaseId);

        var stored = await CaseAsync(x.CaseId);
        Assert.Equal(CaseStatus.InProgress, stored.Status);
        Assert.Null(stored.ResponseDeadlineAt);
        Assert.Equal(1, await AuditCountAsync("ExplanationExpired", x.CaseId));

        await RunJobAsync(x.CaseId);   // повтор безвреден
        Assert.Equal(1, await AuditCountAsync("ExplanationExpired", x.CaseId));
        var thread = await JsonAsync(await SendAsync(x.Moderator.Token, HttpMethod.Get, $"/moderation/cases/{x.CaseId}/explanations"));
        Assert.Equal(3, thread.GetProperty("items").GetArrayLength());   // ответ B так и не пришёл
    }

    [Fact]
    public async Task The_job_does_nothing_before_the_deadline_or_after_everyone_answered()
    {
        var x = await TakenAsync();
        await AskAsync(x.Moderator, x.CaseId);

        await RunJobAsync(x.CaseId);   // срок ещё не вышел
        Assert.Equal(CaseStatus.WaitingResponse, (await CaseAsync(x.CaseId)).Status);

        await AnswerAsync(x.A, x.CaseId, await QuestionForAsync(x.A, x.CaseId));
        await AnswerAsync(x.B, x.CaseId, await QuestionForAsync(x.B, x.CaseId));
        await ExpireAsync(x.CaseId);   // уже вернулось в работу, срок обнулён
        await RunJobAsync(x.CaseId);

        Assert.Equal(CaseStatus.InProgress, (await CaseAsync(x.CaseId)).Status);
        Assert.Equal(0, await AuditCountAsync("ExplanationExpired", x.CaseId));
    }

    [Fact]
    public async Task After_a_return_to_work_the_moderator_can_ask_again()
    {
        var x = await TakenAsync();
        await AskAsync(x.Moderator, x.CaseId, "Opener");
        await AnswerAsync(x.A, x.CaseId, await QuestionForAsync(x.A, x.CaseId));
        Assert.Equal(CaseStatus.InProgress, (await CaseAsync(x.CaseId)).Status);

        var again = await AskAsync(x.Moderator, x.CaseId, "Against", "Уточните, пожалуйста, детали договорённости.");

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(CaseStatus.WaitingResponse, (await CaseAsync(x.CaseId)).Status);
    }

    [Fact]
    public async Task Scheduling_a_question_queues_the_deadline_job()
    {
        var x = await TakenAsync();
        var before = Jobs.Scheduled.Count(j => j.Description.Contains("ExplanationDueAsync"));

        await AskAsync(x.Moderator, x.CaseId);

        var jobs = Jobs.Scheduled.Where(j => j.Description.Contains("ExplanationDueAsync")).ToList();
        Assert.Equal(before + 1, jobs.Count);
        Assert.InRange((jobs[^1].RunAt.UtcDateTime - Now).TotalHours, 47.9, 48.1);
    }
}
