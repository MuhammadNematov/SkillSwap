using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>Подача и отзыв обращений (раздел 8.2 ТЗ) на настоящем приложении и БД: правила, окна, лимиты, доказательства, заморозка отзыва и обмена, видимость.</summary>
[Collection(ApiCollection.Name)]
public class CasesApiTests
{
    private const string GoodText = "Очень понятно объясняет, уроки были полезными и по делу.";
    private const string Complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public CasesApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

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

    private Task<HttpResponseMessage> ApiAsync(MatchPerson who, HttpMethod method, string url, object? body = null) => SendAsync(who.Token, method, url, body);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    private static object Conduct(Guid exchangeId, object? messageIds = null, string? description = Complaint, string category = "Insults", bool fromCall = false) =>
        new { type = "Conduct", category, exchangeId, description, messageIds, fromCall };

    private static object NoShow(Guid exchangeId) => new { type = "NoShowAbuse", category = "SystematicEvasion", exchangeId, description = Complaint };

    private Task<HttpResponseMessage> OpenAsync(MatchPerson who, object body) => ApiAsync(who, HttpMethod.Post, "/cases", body);

    private Task<(MatchTestWorld World, MatchPerson A, MatchPerson B, Guid ExchangeId)> NewExchangeAsync() => ModerationTestData.ExchangeAsync(_factory, _client);

    private Task CloseAsync(Guid exchangeId, DateTime closedAt) => _factory.QueryAsync(async db =>
    {
        await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, ExchangeStatus.Completed).SetProperty(e => e.ClosedAt, closedAt));
        return 0;
    });

    private Task<Case> CaseAsync(Guid id) => _factory.QueryAsync(db => db.Cases.AsNoTracking().SingleAsync(c => c.Id == id));

    private Task<Exchange> ExchangeRowAsync(Guid id) => _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == id));

    private Task<Review> ReviewAsync(Guid exchangeId, Guid author) =>
        _factory.QueryAsync(db => db.Reviews.AsNoTracking().SingleAsync(r => r.ExchangeId == exchangeId && r.AuthorId == author));

    private Task<User> UserAsync(Guid id) => _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private async Task<List<JsonElement>> NotificationsAsync(MatchPerson who, string type)
    {
        var items = (await JsonAsync(await ApiAsync(who, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return items.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    /// <summary>Обмен, где оба написали отзывы: они опубликованы сразу. Возвращает обмен и отзыв B о A.</summary>
    private async Task<(MatchPerson A, MatchPerson B, Guid ExchangeId, Guid ReviewId)> PublishedReviewsAsync()
    {
        var x = await NewExchangeAsync();
        await _factory.QueryAsync(async db =>
        {
            var session = LessonSession.Propose(x.ExchangeId, x.A.Id, x.A.Id, Now.AddHours(5), null, Now, Now.AddDays(30));
            db.Add(session);
            await db.SaveChangesAsync();
            var start = Now.AddDays(-5);
            await db.Sessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(s => s
                .SetProperty(v => v.StartAt, start).SetProperty(v => v.EndAt, start.AddMinutes(45)).SetProperty(v => v.Status, SessionStatus.Completed));
            await db.Exchanges.Where(e => e.Id == x.ExchangeId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExchangeStatus.Completed).SetProperty(e => e.ClosedAt, Now.AddDays(-1)));
            return 0;
        });

        var body = new { scoreKnowledge = 2, scoreExplaining = 2, scorePunctuality = 2, scorePoliteness = 2, text = GoodText };
        Assert.Equal(HttpStatusCode.Created, (await ApiAsync(x.A, HttpMethod.Post, $"/exchanges/{x.ExchangeId}/reviews", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ApiAsync(x.B, HttpMethod.Post, $"/exchanges/{x.ExchangeId}/reviews", body)).StatusCode);
        return (x.A, x.B, x.ExchangeId, (await ReviewAsync(x.ExchangeId, x.B.Id)).Id);
    }

    private static object Dispute(Guid exchangeId, Guid? reviewId, string category = "ReviewUnfair") =>
        new { type = "ReviewDispute", category, exchangeId, reviewId, description = Complaint };

    /// <summary>Идущий урок, в комнате которого сидит <paramref name="inRoom"/>, и ещё один предложенный урок на будущее.</summary>
    private async Task<(Guid Running, Guid Proposed)> StartCallAsync(Guid exchangeId, Guid teacher, Guid inRoom)
    {
        return await _factory.QueryAsync(async db =>
        {
            var running = LessonSession.Propose(exchangeId, teacher, teacher, Now.AddHours(5), null, Now, Now.AddDays(30));
            var proposed = LessonSession.Propose(exchangeId, teacher, teacher, Now.AddDays(2), null, Now, Now.AddDays(30));
            db.AddRange(running, proposed);
            await db.SaveChangesAsync();
            await db.Sessions.Where(s => s.Id == running.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, SessionStatus.InProgress));
            db.Add(SessionAttendance.Open(running.Id, inRoom, Now));
            await db.SaveChangesAsync();
            return (running.Id, proposed.Id);
        });
    }

    // ---------- доступ ----------

    [Fact]
    public async Task Anonymous_and_staff_accounts_cannot_use_cases()
    {
        var x = await NewExchangeAsync();
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, HttpMethod.Post, "/cases", Conduct(x.ExchangeId))).StatusCode);

        var forbidden = await SendAsync(moderator.Token, HttpMethod.Post, "/cases", Conduct(x.ExchangeId));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("regular_account_required", await CodeAsync(forbidden));
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, HttpMethod.Get, "/me/cases")).StatusCode);
    }

    [Fact]
    public async Task A_stranger_cannot_file_on_someone_elses_exchange()
    {
        var x = await NewExchangeAsync();
        var stranger = await x.World.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));

        var response = await OpenAsync(stranger, Conduct(x.ExchangeId));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("exchange_not_found", await CodeAsync(response));
    }

    [Fact]
    public async Task A_deleted_partner_closes_the_way_to_a_case()
    {
        var x = await NewExchangeAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == x.B.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Deleted));
            return 0;
        });

        var response = await OpenAsync(x.A, Conduct(x.ExchangeId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("partner_deleted", await CodeAsync(response));
    }

    // ---------- жалобы ----------

    [Fact]
    public async Task A_conduct_complaint_is_created_with_high_priority_and_an_audit_record_and_the_accused_is_not_told()
    {
        var x = await NewExchangeAsync();
        var m1 = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Первое оскорбление");
        var m2 = await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, "Второе оскорбление");

        var response = await OpenAsync(x.A, Conduct(x.ExchangeId, new[] { m1, m2, m1 }));   // повтор сообщения не дублируется

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("Conduct", dto.GetProperty("type").GetString());
        Assert.Equal("Open", dto.GetProperty("status").GetString());
        Assert.Equal("High", dto.GetProperty("priority").GetString());
        Assert.Equal("Opener", dto.GetProperty("myRole").GetString());
        Assert.True(dto.GetProperty("canWithdraw").GetBoolean());
        Assert.False(dto.GetProperty("fromCall").GetBoolean());
        Assert.Equal(x.B.Id, dto.GetProperty("against").GetProperty("id").GetGuid());
        Assert.Equal(2, dto.GetProperty("evidenceMessageIds").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("decision").ValueKind);

        var id = dto.GetProperty("id").GetGuid();
        var stored = await CaseAsync(id);
        Assert.Equal(x.A.Id, stored.OpenedById);
        Assert.Equal(x.B.Id, stored.AgainstUserId);
        Assert.True(stored.DueAt > Now.AddHours(23) && stored.DueAt < Now.AddHours(25));   // срок реакции 24 часа

        var audit = await _factory.QueryAsync(db => db.AuditLogs.AsNoTracking().SingleAsync(a => a.TargetId == id && a.Action == "CaseOpened"));
        Assert.Equal(x.A.Id, audit.ActorId);

        // Обвиняемый о подаче не знает: ни в списке, ни по ссылке.
        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(x.B, HttpMethod.Get, $"/cases/{id}")).StatusCode);
        var against = await JsonAsync(await ApiAsync(x.B, HttpMethod.Get, "/me/cases?role=Against"));
        Assert.Equal(0, against.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_no_show_complaint_has_normal_priority_and_72_hour_deadline()
    {
        var x = await NewExchangeAsync();

        var dto = await JsonAsync(await OpenAsync(x.A, NoShow(x.ExchangeId)));

        Assert.Equal("Normal", dto.GetProperty("priority").GetString());
        var stored = await CaseAsync(dto.GetProperty("id").GetGuid());
        Assert.True(stored.DueAt > Now.AddHours(71) && stored.DueAt < Now.AddHours(73));
    }

    [Fact]
    public async Task A_minor_on_either_side_makes_the_case_urgent()
    {
        var x = await NewExchangeAsync();
        await _factory.QueryAsync(async db =>
        {
            var birth = new DateOnly(Now.Year - 17, 1, 1);
            await db.Users.Where(u => u.Id == x.B.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DateOfBirth, birth));
            return 0;
        });

        var dto = await JsonAsync(await OpenAsync(x.A, NoShow(x.ExchangeId)));

        Assert.Equal("High", dto.GetProperty("priority").GetString());
    }

    [Theory]
    [InlineData("Conduct", "SystematicEvasion")]
    [InlineData("NoShowAbuse", "Insults")]
    public async Task A_category_that_does_not_fit_the_type_is_rejected(string type, string category)
    {
        var x = await NewExchangeAsync();

        var response = await OpenAsync(x.A, new { type, category, exchangeId = x.ExchangeId, description = Complaint });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("case_category_mismatch", await CodeAsync(response));
    }

    [Theory]
    [InlineData("коротко", "case_description_too_short")]
    [InlineData(null, "case_description_too_short")]
    public async Task A_short_description_is_rejected(string? text, string code)
    {
        var x = await NewExchangeAsync();

        var response = await OpenAsync(x.A, Conduct(x.ExchangeId, description: text));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
    }

    [Fact]
    public async Task A_review_id_in_a_conduct_complaint_is_rejected_and_a_dispute_needs_one()
    {
        var x = await NewExchangeAsync();

        var withReview = await OpenAsync(x.A, new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, reviewId = Guid.NewGuid(), description = Complaint });
        var disputeWithout = await OpenAsync(x.A, Dispute(x.ExchangeId, null));

        Assert.Equal("case_review_not_allowed", await CodeAsync(withReview));
        Assert.Equal(HttpStatusCode.BadRequest, disputeWithout.StatusCode);
        Assert.Equal("case_review_required", await CodeAsync(disputeWithout));
    }

    [Fact]
    public async Task Only_a_conduct_complaint_can_be_filed_from_a_call()
    {
        var x = await NewExchangeAsync();

        var response = await OpenAsync(x.A, new { type = "NoShowAbuse", category = "SystematicEvasion", exchangeId = x.ExchangeId, description = Complaint, fromCall = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("case_from_call_conduct_only", await CodeAsync(response));
    }

    // ---------- сроки подачи ----------

    [Theory]
    [InlineData("Conduct", 89, true)]
    [InlineData("Conduct", 91, false)]
    [InlineData("NoShowAbuse", 29, true)]
    [InlineData("NoShowAbuse", 31, false)]
    public async Task The_filing_window_counts_from_the_closing_of_the_exchange(string type, int daysAgo, bool allowed)
    {
        var x = await NewExchangeAsync();
        await CloseAsync(x.ExchangeId, Now.AddDays(-daysAgo));
        var body = type == "Conduct" ? Conduct(x.ExchangeId) : NoShow(x.ExchangeId);

        var response = await OpenAsync(x.A, body);

        if (allowed)
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("filing_window_closed", await CodeAsync(response));
        }
    }

    // ---------- дубликаты и лимиты ----------

    [Fact]
    public async Task A_second_active_case_of_the_same_type_on_the_same_exchange_is_rejected_but_another_type_is_fine()
    {
        var x = await NewExchangeAsync();
        Assert.Equal(HttpStatusCode.Created, (await OpenAsync(x.A, Conduct(x.ExchangeId))).StatusCode);

        var duplicate = await OpenAsync(x.A, Conduct(x.ExchangeId));
        var other = await OpenAsync(x.A, NoShow(x.ExchangeId));
        var fromPartner = await OpenAsync(x.B, Conduct(x.ExchangeId));   // партнёр может подать свою жалобу

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("case_already_open", await CodeAsync(duplicate));
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fromPartner.StatusCode);
    }

    [Fact]
    public async Task A_withdrawn_case_does_not_block_a_new_one()
    {
        var x = await NewExchangeAsync();
        var first = (await JsonAsync(await OpenAsync(x.A, Conduct(x.ExchangeId)))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await ApiAsync(x.A, HttpMethod.Post, $"/cases/{first}/withdraw")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await OpenAsync(x.A, Conduct(x.ExchangeId))).StatusCode);
    }

    [Fact]
    public async Task No_more_than_three_cases_can_be_in_work_at_once()
    {
        var x = await NewExchangeAsync();
        await _factory.QueryAsync(async db =>
        {
            for (var i = 0; i < CasesInWork; i++)
            {
                db.Cases.Add(Case.Open(CaseType.Conduct, CaseCategory.Insults, x.ExchangeId, null, x.A.Id, x.B.Id, Complaint, false, false, Now.AddDays(-3)));
            }

            await db.SaveChangesAsync();
            await db.Cases.Where(c => c.OpenedById == x.A.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, Now.AddDays(-3)));
            return 0;
        });

        var response = await OpenAsync(x.A, NoShow(x.ExchangeId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("too_many_open_cases", await CodeAsync(response));
    }

    private const int CasesInWork = 3;

    [Fact]
    public async Task No_more_than_five_cases_can_be_filed_in_a_day_even_if_they_were_withdrawn()
    {
        var x = await NewExchangeAsync();
        await _factory.QueryAsync(async db =>
        {
            for (var i = 0; i < 5; i++)
            {
                var withdrawn = Case.Open(CaseType.Conduct, CaseCategory.Insults, x.ExchangeId, null, x.A.Id, x.B.Id, Complaint, false, false, Now);
                withdrawn.Withdraw(x.A.Id);
                db.Cases.Add(withdrawn);
            }

            await db.SaveChangesAsync();
            await db.Cases.Where(c => c.OpenedById == x.A.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, Now.AddHours(-1)));
            return 0;
        });

        var response = await OpenAsync(x.A, Conduct(x.ExchangeId));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("case_rate_limited", await CodeAsync(response));
    }

    // ---------- доказательства ----------

    [Fact]
    public async Task More_than_twenty_messages_as_evidence_are_rejected()
    {
        var x = await NewExchangeAsync();
        var ids = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToList();

        var response = await OpenAsync(x.A, Conduct(x.ExchangeId, ids));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("too_many_evidence", await CodeAsync(response));
    }

    [Fact]
    public async Task Evidence_must_come_from_the_chat_of_the_same_exchange()
    {
        var x = await NewExchangeAsync();
        var other = await NewExchangeAsync();
        var foreign = await ModerationTestData.MessageAsync(_factory, other.ExchangeId, other.A.Id, "Чужое сообщение");

        var response = await OpenAsync(x.A, Conduct(x.ExchangeId, new[] { foreign }));
        var missing = await OpenAsync(x.A, Conduct(x.ExchangeId, new[] { Guid.NewGuid() }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("evidence_not_in_exchange", await CodeAsync(response));
        Assert.Equal("evidence_not_in_exchange", await CodeAsync(missing));
    }

    [Fact]
    public async Task Exactly_twenty_messages_are_accepted_and_come_back_in_the_case()
    {
        var x = await NewExchangeAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add(await ModerationTestData.MessageAsync(_factory, x.ExchangeId, x.B.Id, $"Сообщение {i}"));
        }

        var response = await OpenAsync(x.A, Conduct(x.ExchangeId, ids));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var returned = (await JsonAsync(response)).GetProperty("evidenceMessageIds").EnumerateArray().Select(e => e.GetGuid()).ToHashSet();
        Assert.Equal(ids.ToHashSet(), returned);
    }

    // ---------- спор по отзыву ----------

    [Fact]
    public async Task A_dispute_freezes_the_review_and_takes_it_out_of_the_rating_and_withdrawing_brings_it_back()
    {
        var x = await PublishedReviewsAsync();
        Assert.Equal(1, (await UserAsync(x.A.Id)).RatingCount);

        var response = await OpenAsync(x.A, Dispute(x.ExchangeId, x.ReviewId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("ReviewDispute", dto.GetProperty("type").GetString());
        Assert.Equal("Normal", dto.GetProperty("priority").GetString());
        Assert.Equal(x.B.Id, dto.GetProperty("against").GetProperty("id").GetGuid());
        Assert.Equal(x.ReviewId, dto.GetProperty("reviewId").GetGuid());
        Assert.Equal(ReviewStatus.Frozen, (await ReviewAsync(x.ExchangeId, x.B.Id)).Status);
        Assert.Equal(0, (await UserAsync(x.A.Id)).RatingCount);   // оспариваемый отзыв в рейтинг не входит

        var id = dto.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await ApiAsync(x.A, HttpMethod.Post, $"/cases/{id}/withdraw")).StatusCode);
        Assert.Equal(ReviewStatus.Published, (await ReviewAsync(x.ExchangeId, x.B.Id)).Status);
        Assert.Equal(1, (await UserAsync(x.A.Id)).RatingCount);
    }

    [Fact]
    public async Task A_review_cannot_be_disputed_twice_at_the_same_time_but_can_be_again_after_a_withdrawal()
    {
        var x = await PublishedReviewsAsync();
        var first = (await JsonAsync(await OpenAsync(x.A, Dispute(x.ExchangeId, x.ReviewId)))).GetProperty("id").GetGuid();

        var again = await OpenAsync(x.A, Dispute(x.ExchangeId, x.ReviewId));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("review_already_disputed", await CodeAsync(again));

        await ApiAsync(x.A, HttpMethod.Post, $"/cases/{first}/withdraw");
        Assert.Equal(HttpStatusCode.Created, (await OpenAsync(x.A, Dispute(x.ExchangeId, x.ReviewId))).StatusCode);
    }

    [Fact]
    public async Task A_review_that_was_already_decided_cannot_be_disputed_again()
    {
        var x = await PublishedReviewsAsync();
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        await _factory.QueryAsync(async db =>
        {
            var decided = Case.Open(CaseType.ReviewDispute, CaseCategory.ReviewUnfair, x.ExchangeId, x.ReviewId, x.A.Id, x.B.Id, Complaint, false, false, Now);
            decided.Take(moderator.Id);
            decided.Resolve(moderator.Id, CaseDecision.ReviewKept, false, "Отзыв честный и по делу, оснований скрывать нет.", Now);
            db.Cases.Add(decided);
            await db.SaveChangesAsync();
            return 0;
        });

        var response = await OpenAsync(x.A, Dispute(x.ExchangeId, x.ReviewId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("case_already_exists", await CodeAsync(response));
    }

    [Fact]
    public async Task Only_the_subject_of_a_published_review_can_dispute_it()
    {
        var x = await PublishedReviewsAsync();
        var ownReview = (await ReviewAsync(x.ExchangeId, x.A.Id)).Id;   // отзыв A о B: оспаривать его может только B

        var byAuthor = await OpenAsync(x.A, Dispute(x.ExchangeId, ownReview));
        var unknown = await OpenAsync(x.A, Dispute(x.ExchangeId, Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, byAuthor.StatusCode);
        Assert.Equal("review_not_found", await CodeAsync(byAuthor));
        Assert.Equal("review_not_found", await CodeAsync(unknown));
        Assert.Equal(HttpStatusCode.Created, (await OpenAsync(x.B, Dispute(x.ExchangeId, ownReview))).StatusCode);
    }

    [Fact]
    public async Task A_review_still_hidden_by_the_blind_rule_looks_nonexistent()
    {
        var x = await NewExchangeAsync();
        await _factory.QueryAsync(async db =>
        {
            var session = LessonSession.Propose(x.ExchangeId, x.A.Id, x.A.Id, Now.AddHours(5), null, Now, Now.AddDays(30));
            db.Add(session);
            await db.SaveChangesAsync();
            await db.Sessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, SessionStatus.Completed));
            await db.Exchanges.Where(e => e.Id == x.ExchangeId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExchangeStatus.Completed).SetProperty(e => e.ClosedAt, Now.AddDays(-1)));
            return 0;
        });
        var body = new { scoreKnowledge = 1, scoreExplaining = 1, scorePunctuality = 1, scorePoliteness = 1, text = GoodText };
        await ApiAsync(x.B, HttpMethod.Post, $"/exchanges/{x.ExchangeId}/reviews", body);   // пока ждёт публикации (Pending)
        var pending = (await ReviewAsync(x.ExchangeId, x.B.Id)).Id;

        var response = await OpenAsync(x.A, Dispute(x.ExchangeId, pending));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("review_not_found", await CodeAsync(response));
    }

    [Fact]
    public async Task A_dispute_must_be_filed_within_fourteen_days_after_publication()
    {
        var x = await PublishedReviewsAsync();
        await _factory.QueryAsync(async db =>
        {
            await db.Reviews.Where(r => r.Id == x.ReviewId).ExecuteUpdateAsync(s => s.SetProperty(r => r.PublishedAt, Now.AddDays(-15)));
            return 0;
        });

        var response = await OpenAsync(x.A, Dispute(x.ExchangeId, x.ReviewId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("filing_window_closed", await CodeAsync(response));
        Assert.Equal(ReviewStatus.Published, (await ReviewAsync(x.ExchangeId, x.B.Id)).Status);   // отказ ничего не заморозил
    }

    // ---------- жалоба из звонка ----------

    [Fact]
    public async Task A_complaint_from_outside_the_room_is_rejected_and_freezes_nothing()
    {
        var x = await NewExchangeAsync();

        var response = await OpenAsync(x.A, Conduct(x.ExchangeId, fromCall: true));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_in_call", await CodeAsync(response));
        Assert.Equal(ExchangeStatus.Active, (await ExchangeRowAsync(x.ExchangeId)).Status);
    }

    [Fact]
    public async Task A_complaint_from_a_call_freezes_the_exchange_cancels_open_lessons_closes_the_room_and_tells_both()
    {
        var x = await NewExchangeAsync();
        var (running, proposed) = await StartCallAsync(x.ExchangeId, x.A.Id, inRoom: x.A.Id);

        var response = await OpenAsync(x.A, Conduct(x.ExchangeId, fromCall: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.True(dto.GetProperty("fromCall").GetBoolean());
        Assert.Equal("High", dto.GetProperty("priority").GetString());

        var exchange = await ExchangeRowAsync(x.ExchangeId);
        Assert.Equal(ExchangeStatus.Frozen, exchange.Status);
        Assert.NotNull(exchange.FrozenAt);
        Assert.Equal(SessionStatus.Cancelled, (await _factory.QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.Id == proposed))).Status);
        Assert.Contains($"session-{running:D}", _factory.Rooms.Deleted);
        Assert.Single(await NotificationsAsync(x.A, "ExchangeFrozen"));
        Assert.Single(await NotificationsAsync(x.B, "ExchangeFrozen"));
        Assert.Equal(1, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.TargetId == x.ExchangeId && a.Action == "ExchangeFrozen")));
    }

    [Fact]
    public async Task Withdrawing_a_complaint_from_a_call_unfreezes_the_exchange()
    {
        var x = await NewExchangeAsync();
        await StartCallAsync(x.ExchangeId, x.A.Id, inRoom: x.A.Id);
        var id = (await JsonAsync(await OpenAsync(x.A, Conduct(x.ExchangeId, fromCall: true)))).GetProperty("id").GetGuid();

        var response = await ApiAsync(x.A, HttpMethod.Post, $"/cases/{id}/withdraw");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var exchange = await ExchangeRowAsync(x.ExchangeId);
        Assert.Equal(ExchangeStatus.Active, exchange.Status);
        Assert.Null(exchange.FrozenAt);
        Assert.Single(await NotificationsAsync(x.B, "ExchangeUnfrozen"));
    }

    [Fact]
    public async Task A_complaint_from_a_call_keeps_the_exchange_frozen_while_another_one_from_a_call_is_active()
    {
        var x = await NewExchangeAsync();
        await StartCallAsync(x.ExchangeId, x.A.Id, inRoom: x.A.Id);
        var first = (await JsonAsync(await OpenAsync(x.A, Conduct(x.ExchangeId, fromCall: true)))).GetProperty("id").GetGuid();
        // Второе обращение из звонка: обмен уже заморожен, но жалоба подана партнёром, который сидит в комнате (первое состояние, как будто обмен ещё идёт).
        await _factory.QueryAsync(async db =>
        {
            var other = Case.Open(CaseType.Conduct, CaseCategory.Threats, x.ExchangeId, null, x.B.Id, x.A.Id, Complaint, false, true, Now);
            db.Cases.Add(other);
            await db.SaveChangesAsync();
            return 0;
        });

        await ApiAsync(x.A, HttpMethod.Post, $"/cases/{first}/withdraw");

        Assert.Equal(ExchangeStatus.Frozen, (await ExchangeRowAsync(x.ExchangeId)).Status);
    }

    // ---------- отзыв обращения ----------

    [Fact]
    public async Task The_opener_withdraws_a_case_before_it_is_taken_and_it_is_audited()
    {
        var x = await NewExchangeAsync();
        var id = (await JsonAsync(await OpenAsync(x.A, Conduct(x.ExchangeId)))).GetProperty("id").GetGuid();

        var response = await ApiAsync(x.A, HttpMethod.Post, $"/cases/{id}/withdraw");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await JsonAsync(response);
        Assert.Equal("Withdrawn", dto.GetProperty("status").GetString());
        Assert.False(dto.GetProperty("canWithdraw").GetBoolean());
        Assert.Equal(1, await _factory.QueryAsync(db => db.AuditLogs.CountAsync(a => a.TargetId == id && a.Action == "CaseWithdrawn")));

        var again = await ApiAsync(x.A, HttpMethod.Post, $"/cases/{id}/withdraw");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(again));
    }

    [Fact]
    public async Task Only_the_opener_can_withdraw_and_a_taken_case_cannot_be_withdrawn()
    {
        var x = await NewExchangeAsync();
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var id = (await JsonAsync(await OpenAsync(x.A, Conduct(x.ExchangeId)))).GetProperty("id").GetGuid();

        var byPartner = await ApiAsync(x.B, HttpMethod.Post, $"/cases/{id}/withdraw");
        Assert.Equal(HttpStatusCode.NotFound, byPartner.StatusCode);
        Assert.Equal("case_not_found", await CodeAsync(byPartner));

        await _factory.QueryAsync(async db =>
        {
            var taken = await db.Cases.SingleAsync(c => c.Id == id);
            taken.Take(moderator.Id);
            await db.SaveChangesAsync();
            return 0;
        });

        var late = await ApiAsync(x.A, HttpMethod.Post, $"/cases/{id}/withdraw");
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("invalid_state_transition", await CodeAsync(late));
        Assert.Equal(CaseStatus.InProgress, (await CaseAsync(id)).Status);
    }

    // ---------- чтение ----------

    [Fact]
    public async Task The_accused_sees_a_case_only_after_it_is_taken_and_a_stranger_never()
    {
        var x = await NewExchangeAsync();
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var stranger = await x.World.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        var id = (await JsonAsync(await OpenAsync(x.A, Conduct(x.ExchangeId)))).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(x.B, HttpMethod.Get, $"/cases/{id}")).StatusCode);
        await _factory.QueryAsync(async db =>
        {
            (await db.Cases.SingleAsync(c => c.Id == id)).Take(moderator.Id);
            await db.SaveChangesAsync();
            return 0;
        });

        var seen = await ApiAsync(x.B, HttpMethod.Get, $"/cases/{id}");
        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        var dto = await JsonAsync(seen);
        Assert.Equal("Against", dto.GetProperty("myRole").GetString());
        Assert.False(dto.GetProperty("canWithdraw").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(stranger, HttpMethod.Get, $"/cases/{id}")).StatusCode);
    }

    [Fact]
    public async Task My_cases_are_listed_by_role_newest_first_and_the_decision_is_hidden_until_it_is_made()
    {
        var x = await NewExchangeAsync();
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var first = (await JsonAsync(await OpenAsync(x.A, Conduct(x.ExchangeId)))).GetProperty("id").GetGuid();
        var second = (await JsonAsync(await OpenAsync(x.A, NoShow(x.ExchangeId)))).GetProperty("id").GetGuid();
        await _factory.QueryAsync(async db =>
        {
            (await db.Cases.SingleAsync(c => c.Id == first)).Take(moderator.Id);
            await db.SaveChangesAsync();
            return 0;
        });

        var opened = await JsonAsync(await ApiAsync(x.A, HttpMethod.Get, "/me/cases?role=Opened"));
        var all = await JsonAsync(await ApiAsync(x.A, HttpMethod.Get, "/me/cases"));
        var againstB = await JsonAsync(await ApiAsync(x.B, HttpMethod.Get, "/me/cases?role=Against"));
        var againstA = await JsonAsync(await ApiAsync(x.A, HttpMethod.Get, "/me/cases?role=Against"));

        Assert.Equal(2, opened.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(second, opened.GetProperty("items")[0].GetProperty("id").GetGuid());   // новые сверху
        Assert.Equal(first, againstB.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());   // второе ещё не взято
        Assert.Equal(0, againstA.GetProperty("totalCount").GetInt32());
        Assert.All(opened.GetProperty("items").EnumerateArray(), i => Assert.Equal(JsonValueKind.Null, i.GetProperty("decision").ValueKind));
    }

    [Fact]
    public async Task The_decision_and_its_reason_appear_for_both_sides_once_the_case_is_resolved()
    {
        var x = await PublishedReviewsAsync();
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);
        var id = (await JsonAsync(await OpenAsync(x.A, Dispute(x.ExchangeId, x.ReviewId)))).GetProperty("id").GetGuid();
        await _factory.QueryAsync(async db =>
        {
            var c = await db.Cases.SingleAsync(v => v.Id == id);
            c.Take(moderator.Id);
            c.Resolve(moderator.Id, CaseDecision.ReviewKept, false, "Отзыв честный и по делу, оснований скрывать его нет.", Now);
            await db.SaveChangesAsync();
            return 0;
        });

        foreach (var who in new[] { x.A, x.B })
        {
            var dto = await JsonAsync(await ApiAsync(who, HttpMethod.Get, $"/cases/{id}"));
            Assert.Equal("Resolved", dto.GetProperty("status").GetString());
            Assert.Equal("ReviewKept", dto.GetProperty("decision").GetString());
            Assert.Contains("честный", dto.GetProperty("decisionReason").GetString());
        }
    }

    [Theory]
    [InlineData("/me/cases?page=0")]
    [InlineData("/me/cases?pageSize=500")]
    public async Task Bad_paging_is_a_clear_error(string url)
    {
        var x = await NewExchangeAsync();

        var response = await ApiAsync(x.A, HttpMethod.Get, url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_paging", await CodeAsync(response));
    }
}
