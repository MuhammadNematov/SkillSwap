using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Api.IntegrationTests.Matching;
using SkillSwap.Application.Abstractions;
using SkillSwap.Application.Account;
using SkillSwap.Application.Reviews;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;
using SkillSwap.Infrastructure.Jobs;

namespace SkillSwap.Api.IntegrationTests.Moderation;

/// <summary>Отзывы (раздел 8.1 ТЗ) на настоящем приложении и БД: право на отзыв, слепая публикация, рейтинг, профиль, главная, уведомления, выгрузка.</summary>
[Collection(ApiCollection.Name)]
public class ReviewsApiTests
{
    private const string GoodText = "Очень понятно объясняет, уроки были полезными и по делу.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public ReviewsApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private static object Body(int k = 5, int e = 4, int p = 3, int po = 4, string? text = GoodText) =>
        new { scoreKnowledge = k, scoreExplaining = e, scorePunctuality = p, scorePoliteness = po, text };

    private async Task<HttpResponseMessage> ApiAsync(MatchPerson who, HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, "/api/v1" + url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private Task<HttpResponseMessage> WriteAsync(MatchPerson who, Guid exchangeId, object? body = null) =>
        ApiAsync(who, HttpMethod.Post, $"/exchanges/{exchangeId}/reviews", body ?? Body());

    private async Task<JsonElement> StateAsync(MatchPerson who, Guid exchangeId) =>
        await JsonAsync(await ApiAsync(who, HttpMethod.Get, $"/exchanges/{exchangeId}/reviews"));

    private async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    // ---------- данные ----------

    private Task<(MatchTestWorld World, MatchPerson A, MatchPerson B, Guid ExchangeId)> NewExchangeAsync() =>
        ModerationTestData.ExchangeAsync(_factory, _client);

    /// <summary>Засчитанный (Completed) урок в обмене: преподаёт <paramref name="teacher"/>.</summary>
    private async Task CompletedSessionAsync(Guid exchangeId, Guid teacher, int daysAgo = 5)
    {
        await _factory.QueryAsync(async db =>
        {
            var start = Now.AddDays(-daysAgo);
            var session = LessonSession.Propose(exchangeId, teacher, teacher, Now.AddHours(5), null, Now, Now.AddDays(30));
            db.Add(session);
            await db.SaveChangesAsync();
            await db.Sessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.StartAt, start).SetProperty(x => x.EndAt, start.AddMinutes(45))
                .SetProperty(x => x.Status, SessionStatus.Completed));
            return 0;
        });
    }

    /// <summary>Окончательная неявка <paramref name="culprit"/> (итог подведён при закрытии комнаты).</summary>
    private async Task FinalNoShowsAsync(Guid exchangeId, Guid teacher, Guid culprit, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await _factory.QueryAsync(async db =>
            {
                var start = Now.AddDays(-3 - i);
                var session = LessonSession.Propose(exchangeId, teacher, teacher, Now.AddHours(5), null, Now, Now.AddDays(30));
                db.Add(session);
                await db.SaveChangesAsync();
                await db.Sessions.Where(s => s.Id == session.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.StartAt, start).SetProperty(x => x.EndAt, start.AddMinutes(45))
                    .SetProperty(x => x.Status, SessionStatus.NoShow).SetProperty(x => x.NoShowUserId, culprit)
                    .SetProperty(x => x.RoomClosedAt, start.AddMinutes(75)));
                return 0;
            });
        }
    }

    /// <summary>Закрывает обмен напрямую в БД (быстрее и точнее, чем через API): статус, время закрытия, кто прервал.</summary>
    private Task CloseAsync(Guid exchangeId, ExchangeStatus status = ExchangeStatus.Completed, DateTime? closedAt = null, Guid? abortedBy = null) =>
        _factory.QueryAsync(async db =>
        {
            var at = closedAt ?? Now.AddDays(-1);
            await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, status).SetProperty(e => e.ClosedAt, at).SetProperty(e => e.AbortedById, abortedBy));
            return 0;
        });

    private Task SetStatusAsync(Guid userId, UserStatus status) => _factory.QueryAsync(async db =>
    {
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, status));
        return 0;
    });

    private Task<User> UserAsync(Guid id) => _factory.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<Review> ReviewAsync(Guid exchangeId, Guid author) =>
        _factory.QueryAsync(db => db.Reviews.AsNoTracking().SingleAsync(r => r.ExchangeId == exchangeId && r.AuthorId == author));

    /// <summary>Обмен, закрытый вчера, с одним засчитанным уроком: оба могут писать.</summary>
    private async Task<(MatchTestWorld World, MatchPerson A, MatchPerson B, Guid ExchangeId)> ReadyAsync()
    {
        var x = await NewExchangeAsync();
        await CompletedSessionAsync(x.ExchangeId, x.A.Id);
        await CloseAsync(x.ExchangeId);
        return x;
    }

    private async Task<List<JsonElement>> NotificationsAsync(MatchPerson who, string type)
    {
        var items = (await JsonAsync(await ApiAsync(who, HttpMethod.Get, "/notifications?pageSize=50"))).GetProperty("items");
        return items.EnumerateArray().Where(i => i.GetProperty("type").GetString() == type).ToList();
    }

    private async Task RunJobAsync(Func<ReviewJobs, Task> job)
    {
        using var scope = _factory.Services.CreateScope();
        await job(scope.ServiceProvider.GetRequiredService<ReviewJobs>());
    }

    // ---------- право на отзыв ----------

    [Fact]
    public async Task While_the_exchange_is_active_nothing_can_be_written()
    {
        var (_, a, _, exchangeId) = await NewExchangeAsync();

        var state = await StateAsync(a, exchangeId);

        Assert.False(state.GetProperty("canWrite").GetBoolean());
        Assert.Equal("exchange_not_closed", state.GetProperty("unavailableReason").GetString());
        var response = await WriteAsync(a, exchangeId);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("exchange_not_closed", await CodeAsync(response));
    }

    [Fact]
    public async Task After_a_normal_end_with_a_counted_session_both_can_write_and_the_deadline_is_fourteen_days_after_closing()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();
        var closed = (await _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == exchangeId))).ClosedAt!.Value;

        foreach (var who in new[] { a, b })
        {
            var state = await StateAsync(who, exchangeId);
            Assert.True(state.GetProperty("canWrite").GetBoolean());
            Assert.Equal(JsonValueKind.Null, state.GetProperty("unavailableReason").ValueKind);
            Assert.False(state.GetProperty("oneSided").GetBoolean());
            Assert.False(state.GetProperty("isFullExchange").GetBoolean());
            Assert.Equal(closed.AddDays(14), state.GetProperty("deadlineAt").GetDateTime(), TimeSpan.FromSeconds(1));
            Assert.Equal(JsonValueKind.Null, state.GetProperty("myReview").ValueKind);
        }
    }

    [Fact]
    public async Task Rule_four_an_exchange_without_counted_sessions_and_without_no_shows_has_no_reviews()
    {
        var (_, a, b, exchangeId) = await NewExchangeAsync();
        await CloseAsync(exchangeId, ExchangeStatus.Aborted, abortedBy: a.Id);

        foreach (var who in new[] { a, b })
        {
            Assert.Equal("no_counted_sessions", (await StateAsync(who, exchangeId)).GetProperty("unavailableReason").GetString());
        }

        var response = await WriteAsync(a, exchangeId);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no_counted_sessions", await CodeAsync(response));
    }

    [Fact]
    public async Task An_aborted_exchange_with_a_counted_session_gives_both_the_right()
    {
        var (_, a, b, exchangeId) = await NewExchangeAsync();
        await CompletedSessionAsync(exchangeId, b.Id);
        await CloseAsync(exchangeId, ExchangeStatus.Aborted, abortedBy: a.Id);

        Assert.True((await StateAsync(a, exchangeId)).GetProperty("canWrite").GetBoolean());
        Assert.True((await StateAsync(b, exchangeId)).GetProperty("canWrite").GetBoolean());
    }

    [Fact]
    public async Task A_block_does_not_take_the_right_to_review_away_from_either_side_and_does_not_reveal_the_block()
    {
        var (_, blocker, blocked, exchangeId) = await NewExchangeAsync();
        await CompletedSessionAsync(exchangeId, blocker.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await ApiAsync(blocker, HttpMethod.Post, $"/users/{blocked.Id}/block")).StatusCode);   // обмен прерван блокировкой

        var exchange = await _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == exchangeId));
        Assert.Equal(ExchangeStatus.Aborted, exchange.Status);
        Assert.Equal(Exchange.AbortReasonBlocked, exchange.AbortReason);
        foreach (var who in new[] { blocker, blocked })
        {
            var state = await StateAsync(who, exchangeId);
            Assert.True(state.GetProperty("canWrite").GetBoolean());
            Assert.False(state.GetProperty("oneSided").GetBoolean());
        }

        // Заблокированный пишет отзыв, блокировавший пишет свой: нарушитель не избежал отзыва, заблокировав партнёра первым.
        Assert.Equal(HttpStatusCode.Created, (await WriteAsync(blocked, exchangeId, Body(1, 1, 1, 1, "Заблокировал меня сразу после уроков, это нечестно."))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await WriteAsync(blocker, exchangeId)).StatusCode);
        Assert.Equal(ReviewStatus.Published, (await ReviewAsync(exchangeId, blocker.Id)).Status);   // оба написали: опубликованы вместе
        Assert.Equal(ReviewStatus.Published, (await ReviewAsync(exchangeId, blocked.Id)).Status);
    }

    [Fact]
    public async Task A_block_without_any_counted_session_still_gives_no_review_by_rule_four()
    {
        var (_, blocker, blocked, exchangeId) = await NewExchangeAsync();

        await ApiAsync(blocker, HttpMethod.Post, $"/users/{blocked.Id}/block");

        Assert.Equal("no_counted_sessions", (await StateAsync(blocker, exchangeId)).GetProperty("unavailableReason").GetString());
        Assert.Equal("no_counted_sessions", (await StateAsync(blocked, exchangeId)).GetProperty("unavailableReason").GetString());
    }

    [Fact]
    public async Task Rule_three_the_victim_of_three_final_no_shows_writes_alone_and_the_culprit_cannot()
    {
        var (_, a, b, exchangeId) = await NewExchangeAsync();
        await FinalNoShowsAsync(exchangeId, a.Id, culprit: b.Id, 3);
        await CloseAsync(exchangeId, ExchangeStatus.Aborted, abortedBy: b.Id);
        await _factory.QueryAsync(async db =>
        {
            await db.Exchanges.Where(e => e.Id == exchangeId).ExecuteUpdateAsync(s => s.SetProperty(e => e.AbortedForNoShow, true));
            return 0;
        });

        var victim = await StateAsync(a, exchangeId);
        var culprit = await StateAsync(b, exchangeId);

        Assert.True(victim.GetProperty("canWrite").GetBoolean());
        Assert.True(victim.GetProperty("oneSided").GetBoolean());
        Assert.False(culprit.GetProperty("canWrite").GetBoolean());
        Assert.Equal("no_counted_sessions", culprit.GetProperty("unavailableReason").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(b, exchangeId)).StatusCode);
    }

    [Fact]
    public async Task A_one_sided_review_is_published_at_once_and_counts_in_the_rating()
    {
        var (_, a, b, exchangeId) = await NewExchangeAsync();
        await FinalNoShowsAsync(exchangeId, a.Id, culprit: b.Id, 3);
        await CloseAsync(exchangeId, ExchangeStatus.Aborted, abortedBy: b.Id);

        var response = await WriteAsync(a, exchangeId, Body(1, 2, 1, 2, "Партнёр три раза не пришёл на уроки и не предупреждал."));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var review = await JsonAsync(response);
        Assert.Equal("Published", review.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, review.GetProperty("publishAt").ValueKind);
        var target = await UserAsync(b.Id);
        Assert.Equal(1, target.RatingCount);
        Assert.Equal(1.5m, target.RatingAvg);
        Assert.Single(await NotificationsAsync(b, "ReviewPublished"));
    }

    [Fact]
    public async Task Both_victims_of_each_others_no_shows_write_by_the_blind_rules()
    {
        var (_, a, b, exchangeId) = await NewExchangeAsync();
        await FinalNoShowsAsync(exchangeId, a.Id, culprit: b.Id, 3);
        await FinalNoShowsAsync(exchangeId, b.Id, culprit: a.Id, 3);
        await CloseAsync(exchangeId, ExchangeStatus.Aborted);

        Assert.False((await StateAsync(a, exchangeId)).GetProperty("oneSided").GetBoolean());

        await WriteAsync(a, exchangeId);

        Assert.Equal(ReviewStatus.Pending, (await ReviewAsync(exchangeId, a.Id)).Status);   // партнёр тоже вправе написать: ждём
    }

    [Fact]
    public async Task A_deleted_partner_takes_the_right_to_review_him()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();
        await SetStatusAsync(b.Id, UserStatus.Deleted);

        var state = await StateAsync(a, exchangeId);

        Assert.False(state.GetProperty("canWrite").GetBoolean());
        Assert.Equal("partner_deleted", state.GetProperty("unavailableReason").GetString());
        Assert.Equal("partner_deleted", await CodeAsync(await WriteAsync(a, exchangeId)));
    }

    [Fact]
    public async Task The_deadline_is_fourteen_days_after_closing_and_not_a_day_longer()
    {
        var (_, a, b, exchangeId) = await NewExchangeAsync();
        await CompletedSessionAsync(exchangeId, a.Id);
        await CloseAsync(exchangeId, closedAt: Now.AddDays(-14).AddMinutes(10));   // осталось десять минут

        Assert.True((await StateAsync(a, exchangeId)).GetProperty("canWrite").GetBoolean());

        await CloseAsync(exchangeId, closedAt: Now.AddDays(-14).AddMinutes(-10));   // срок вышел десять минут назад

        var state = await StateAsync(a, exchangeId);
        Assert.False(state.GetProperty("canWrite").GetBoolean());
        Assert.Equal("review_window_closed", state.GetProperty("unavailableReason").GetString());
        Assert.Equal("review_window_closed", await CodeAsync(await WriteAsync(b, exchangeId)));
    }

    [Fact]
    public async Task The_full_exchange_mark_goes_to_reviews_of_a_normally_ended_exchange_where_each_taught_three_lessons()
    {
        var (_, a, b, exchangeId) = await NewExchangeAsync();
        for (var i = 0; i < 3; i++)
        {
            await CompletedSessionAsync(exchangeId, a.Id, 10 + i);
            await CompletedSessionAsync(exchangeId, b.Id, 20 + i);
        }

        await CloseAsync(exchangeId);

        Assert.True((await StateAsync(a, exchangeId)).GetProperty("isFullExchange").GetBoolean());
        var review = await JsonAsync(await WriteAsync(a, exchangeId));
        Assert.True(review.GetProperty("isFullExchange").GetBoolean());

        var (_, c, d, other) = await NewExchangeAsync();
        for (var i = 0; i < 3; i++)
        {
            await CompletedSessionAsync(other, c.Id, 10 + i);
        }

        await CloseAsync(other);   // второй преподавал меньше трёх раз
        Assert.False((await StateAsync(c, other)).GetProperty("isFullExchange").GetBoolean());
    }

    [Fact]
    public async Task Only_participants_see_the_reviews_of_an_exchange_and_only_regular_accounts()
    {
        var (world, a, _, exchangeId) = await ReadyAsync();
        var stranger = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);

        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(stranger, HttpMethod.Get, $"/exchanges/{exchangeId}/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await WriteAsync(stranger, exchangeId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(a, HttpMethod.Get, $"/exchanges/{Guid.NewGuid()}/reviews")).StatusCode);
        var staffToken = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/exchanges/{exchangeId}/reviews");
        staffToken.Headers.Authorization = new AuthenticationHeaderValue("Bearer", moderator.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(staffToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"/api/v1/exchanges/{exchangeId}/reviews")).StatusCode);
    }

    // ---------- написать и слепая публикация ----------

    [Fact]
    public async Task A_written_review_is_hidden_from_the_partner_until_the_partner_writes_or_seven_days_pass()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();

        var response = await WriteAsync(a, exchangeId, Body(5, 4, 3, 4));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var mine = await JsonAsync(response);
        Assert.Equal("Pending", mine.GetProperty("status").GetString());
        Assert.Equal(4m, mine.GetProperty("average").GetDecimal());
        Assert.Equal(a.Id, mine.GetProperty("author").GetProperty("id").GetGuid());
        Assert.Equal(b.Id, mine.GetProperty("targetId").GetGuid());
        var stored = await ReviewAsync(exchangeId, a.Id);
        Assert.Equal(stored.PublishDueAt, mine.GetProperty("publishAt").GetDateTime(), TimeSpan.FromMilliseconds(1));   // писали по часам приложения: срок от них, а не от времени записи в БД
        Assert.Equal(Now.AddDays(7), stored.PublishDueAt, TimeSpan.FromSeconds(2));

        var partnerView = await StateAsync(b, exchangeId);
        Assert.Equal(JsonValueKind.Null, partnerView.GetProperty("reviewAboutMe").ValueKind);   // слепой: партнёр не видит ни текста, ни факта
        Assert.True(partnerView.GetProperty("canWrite").GetBoolean());
        Assert.Equal(0, (await UserAsync(b.Id)).RatingCount);                                    // в рейтинг пока не входит
        Assert.Empty(await NotificationsAsync(b, "ReviewPublished"));
        var own = await StateAsync(a, exchangeId);
        Assert.False(own.GetProperty("canWrite").GetBoolean());
        Assert.Equal("review_already_exists", own.GetProperty("unavailableReason").GetString());
        Assert.True(own.GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task When_the_partner_writes_too_both_reviews_become_visible_at_the_same_moment_and_ratings_update_for_both()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();
        await WriteAsync(a, exchangeId, Body(5, 5, 5, 5, "Отличный партнёр, всё чётко и вовремя, рекомендую."));

        var second = await WriteAsync(b, exchangeId, Body(3, 3, 3, 3, "Нормально, но были опоздания на несколько минут."));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("Published", (await JsonAsync(second)).GetProperty("status").GetString());
        var first = await ReviewAsync(exchangeId, a.Id);
        var other = await ReviewAsync(exchangeId, b.Id);
        Assert.Equal(ReviewStatus.Published, first.Status);
        Assert.Equal(ReviewStatus.Published, other.Status);
        Assert.Equal(first.PublishedAt, other.PublishedAt);   // одновременно

        var aboutA = (await StateAsync(a, exchangeId)).GetProperty("reviewAboutMe");
        var aboutB = (await StateAsync(b, exchangeId)).GetProperty("reviewAboutMe");
        Assert.Equal(3m, aboutA.GetProperty("average").GetDecimal());                     // обо мне написал партнёр
        Assert.Equal(b.Id, aboutA.GetProperty("author").GetProperty("id").GetGuid());
        Assert.Equal(5m, aboutB.GetProperty("average").GetDecimal());

        var userA = await UserAsync(a.Id);
        var userB = await UserAsync(b.Id);
        Assert.Equal((1, 3m), (userA.RatingCount, userA.RatingAvg));
        Assert.Equal((1, 5m), (userB.RatingCount, userB.RatingAvg));
        Assert.Single(await NotificationsAsync(a, "ReviewPublished"));
        Assert.Single(await NotificationsAsync(b, "ReviewPublished"));
    }

    [Fact]
    public async Task A_second_review_by_the_same_author_is_a_conflict_and_changes_nothing()
    {
        var (_, a, _, exchangeId) = await ReadyAsync();
        await WriteAsync(a, exchangeId, Body(5, 5, 5, 5));

        var again = await WriteAsync(a, exchangeId, Body(1, 1, 1, 1));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("review_already_exists", await CodeAsync(again));
        Assert.Equal(5, (await ReviewAsync(exchangeId, a.Id)).ScoreKnowledge);
        Assert.Equal(1, await _factory.QueryAsync(db => db.Reviews.CountAsync(r => r.ExchangeId == exchangeId)));
    }

    [Theory]
    [InlineData(0, 4, 4, 4, "review_score_out_of_range")]
    [InlineData(4, 6, 4, 4, "review_score_out_of_range")]
    [InlineData(4, 4, -1, 4, "review_score_out_of_range")]
    public async Task Scores_outside_one_to_five_are_a_bad_request(int k, int e, int p, int po, string code)
    {
        var (_, a, _, exchangeId) = await ReadyAsync();

        var response = await WriteAsync(a, exchangeId, Body(k, e, p, po));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
        Assert.Equal(0, await _factory.QueryAsync(db => db.Reviews.CountAsync(r => r.ExchangeId == exchangeId)));
    }

    [Fact]
    public async Task The_text_must_have_twenty_to_a_thousand_characters()
    {
        var (_, a, _, exchangeId) = await ReadyAsync();

        Assert.Equal("review_text_too_short", await CodeAsync(await WriteAsync(a, exchangeId, Body(text: new string('я', 19)))));
        Assert.Equal("review_text_too_short", await CodeAsync(await WriteAsync(a, exchangeId, Body(text: null))));
        Assert.Equal("review_text_too_long", await CodeAsync(await WriteAsync(a, exchangeId, Body(text: new string('я', 1001)))));
        Assert.Equal(HttpStatusCode.Created, (await WriteAsync(a, exchangeId, Body(text: new string('я', 1000)))).StatusCode);
    }

    [Fact]
    public async Task Two_partners_writing_at_the_same_moment_both_end_up_published_with_correct_ratings()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();

        var responses = await Task.WhenAll(WriteAsync(a, exchangeId, Body(5, 5, 5, 5)), WriteAsync(b, exchangeId, Body(1, 1, 1, 1, "Не понравилось: опаздывал и плохо готовился к урокам.")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal(ReviewStatus.Published, (await ReviewAsync(exchangeId, a.Id)).Status);
        Assert.Equal(ReviewStatus.Published, (await ReviewAsync(exchangeId, b.Id)).Status);
        Assert.Equal((1, 1m), ((await UserAsync(a.Id)).RatingCount, (await UserAsync(a.Id)).RatingAvg));
        Assert.Equal((1, 5m), ((await UserAsync(b.Id)).RatingCount, (await UserAsync(b.Id)).RatingAvg));
    }

    [Fact]
    public async Task The_same_author_sending_the_review_many_times_at_once_creates_exactly_one()
    {
        var (_, a, _, exchangeId) = await ReadyAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => WriteAsync(a, exchangeId)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await _factory.QueryAsync(db => db.Reviews.CountAsync(r => r.ExchangeId == exchangeId)));
    }

    // ---------- правка ----------

    [Fact]
    public async Task An_unpublished_review_can_be_edited_and_the_publication_time_does_not_move()
    {
        var (_, a, _, exchangeId) = await ReadyAsync();
        var created = await JsonAsync(await WriteAsync(a, exchangeId));

        var response = await ApiAsync(a, HttpMethod.Put, $"/exchanges/{exchangeId}/reviews", Body(2, 2, 2, 2, "Подумал и поменял оценку: уроки были слабее, чем казалось."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edited = await JsonAsync(response);
        Assert.Equal(2, edited.GetProperty("scoreKnowledge").GetInt32());
        Assert.Equal("Подумал и поменял оценку: уроки были слабее, чем казалось.", edited.GetProperty("text").GetString());
        Assert.Equal(created.GetProperty("publishAt").GetDateTime(), edited.GetProperty("publishAt").GetDateTime());
        Assert.Equal(created.GetProperty("id").GetGuid(), edited.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_published_review_cannot_be_edited_and_a_missing_one_is_not_found()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(a, HttpMethod.Put, $"/exchanges/{exchangeId}/reviews", Body())).StatusCode);
        Assert.Equal("review_not_found", await CodeAsync(await ApiAsync(a, HttpMethod.Put, $"/exchanges/{exchangeId}/reviews", Body())));

        await WriteAsync(a, exchangeId);
        await WriteAsync(b, exchangeId);   // оба написали: отзывы опубликованы

        var response = await ApiAsync(a, HttpMethod.Put, $"/exchanges/{exchangeId}/reviews", Body(1, 1, 1, 1));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("review_already_published", await CodeAsync(response));
        Assert.Equal(5, (await ReviewAsync(exchangeId, a.Id)).ScoreKnowledge);
        Assert.False((await StateAsync(a, exchangeId)).GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task An_edit_is_validated_like_a_new_review_and_is_refused_after_the_deadline()
    {
        var (_, a, _, exchangeId) = await ReadyAsync();
        await WriteAsync(a, exchangeId);

        Assert.Equal("review_text_too_short", await CodeAsync(await ApiAsync(a, HttpMethod.Put, $"/exchanges/{exchangeId}/reviews", Body(text: "коротко"))));
        Assert.Equal("review_score_out_of_range", await CodeAsync(await ApiAsync(a, HttpMethod.Put, $"/exchanges/{exchangeId}/reviews", Body(7, 1, 1, 1))));
        Assert.Equal(5, (await ReviewAsync(exchangeId, a.Id)).ScoreKnowledge);

        await CloseAsync(exchangeId, closedAt: Now.AddDays(-15));   // срок вышел, а отзыв ещё не опубликован
        var late = await ApiAsync(a, HttpMethod.Put, $"/exchanges/{exchangeId}/reviews", Body(1, 1, 1, 1));
        Assert.Equal("review_window_closed", await CodeAsync(late));
        Assert.False((await StateAsync(a, exchangeId)).GetProperty("canEdit").GetBoolean());
    }

    // ---------- публикация по сроку ----------

    [Fact]
    public async Task A_review_waiting_for_the_partner_is_scheduled_for_publication_seven_days_after_it_was_written()
    {
        var (_, a, _, exchangeId) = await ReadyAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<IBackgroundJobs>();
        var before = jobs.Scheduled.Count;

        await WriteAsync(a, exchangeId);

        var job = Assert.Single(jobs.Scheduled.Skip(before), j => j.Description.Contains(nameof(ReviewJobs.PublishDueAsync)));
        var stored = await ReviewAsync(exchangeId, a.Id);
        Assert.Equal(stored.PublishDueAt, job.RunAt.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.Equal(Now.AddDays(7), stored.PublishDueAt, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task The_job_publishes_a_review_after_seven_days_updates_the_rating_notifies_the_target_and_is_idempotent()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();
        await WriteAsync(a, exchangeId, Body(4, 4, 4, 4));
        var review = await ReviewAsync(exchangeId, a.Id);

        await RunJobAsync(j => j.PublishDueAsync(review.Id));   // рано: срок не вышел
        Assert.Equal(ReviewStatus.Pending, (await ReviewAsync(exchangeId, a.Id)).Status);
        Assert.Equal(0, (await UserAsync(b.Id)).RatingCount);

        await _factory.QueryAsync(async db =>
        {
            await db.Reviews.Where(r => r.Id == review.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.PublishDueAt, Now.AddMinutes(-1)));
            return 0;
        });
        await RunJobAsync(j => j.PublishDueAsync(review.Id));
        await RunJobAsync(j => j.PublishDueAsync(review.Id));   // повтор безвреден

        var published = await ReviewAsync(exchangeId, a.Id);
        Assert.Equal(ReviewStatus.Published, published.Status);
        Assert.NotNull(published.PublishedAt);
        Assert.Equal((1, 4m), ((await UserAsync(b.Id)).RatingCount, (await UserAsync(b.Id)).RatingAvg));
        Assert.Single(await NotificationsAsync(b, "ReviewPublished"));
        Assert.Equal("Published", (await StateAsync(b, exchangeId)).GetProperty("reviewAboutMe").GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_job_does_nothing_for_an_already_published_or_missing_review()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();
        await WriteAsync(a, exchangeId);
        await WriteAsync(b, exchangeId);   // оба опубликованы
        var review = await ReviewAsync(exchangeId, a.Id);
        var count = (await NotificationsAsync(b, "ReviewPublished")).Count;

        await RunJobAsync(j => j.PublishDueAsync(review.Id));
        await RunJobAsync(j => j.PublishDueAsync(Guid.NewGuid()));

        Assert.Equal(count, (await NotificationsAsync(b, "ReviewPublished")).Count);
        Assert.Equal(review.PublishedAt, (await ReviewAsync(exchangeId, a.Id)).PublishedAt);
    }

    // ---------- рейтинг ----------

    private async Task<(MatchPerson Target, List<MatchPerson> Authors, MatchTestWorld World)> ReviewedByAsync(params int[][] scoresByAuthor)
    {
        var world = await MatchTestWorld.CreateAsync(_factory);   // общий каталог навыков у цели и всех авторов
        var target = await world.UserAsync((1, SkillLevel.Beginner), (0, SkillLevel.Beginner));
        var authors = new List<MatchPerson>();
        foreach (var scores in scoresByAuthor)
        {
            var author = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
            // Обмен автора с целью: заявка автор → цель (у цели навык 1, у автора навык 0, как у всех пар тестов).
            var created = await ApiAsync(author, HttpMethod.Post, "/exchange-requests",
                new { toUserId = target.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
            var exchangeId = (await JsonAsync(await ApiAsync(target, HttpMethod.Post, $"/exchange-requests/{requestId}/accept"))).GetProperty("exchangeId").GetGuid();
            await CompletedSessionAsync(exchangeId, author.Id);
            await CloseAsync(exchangeId);
            await FinalizeAsync(author, exchangeId, scores);
            authors.Add(author);
        }

        return (target, authors, world);
    }

    /// <summary>Автор пишет отзыв, цель отвечает любым отзывом: оба публикуются.</summary>
    private async Task FinalizeAsync(MatchPerson author, Guid exchangeId, int[] scores)
    {
        var response = await WriteAsync(author, exchangeId, Body(scores[0], scores[1], scores[2], scores[3]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await _factory.QueryAsync(async db =>
        {
            var review = await db.Reviews.SingleAsync(r => r.ExchangeId == exchangeId && r.AuthorId == author.Id);
            await db.Reviews.Where(r => r.Id == review.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.PublishDueAt, Now.AddMinutes(-1)));
            return 0;
        });
        var id = (await ReviewAsync(exchangeId, author.Id)).Id;
        await RunJobAsync(j => j.PublishDueAsync(id));
    }

    [Fact]
    public async Task The_rating_is_the_mean_of_the_means_and_a_user_with_fewer_than_three_reviews_stays_a_newcomer()
    {
        var (target, _, world) = await ReviewedByAsync([5, 5, 5, 5], [4, 4, 4, 4]);

        var two = await UserAsync(target.Id);
        Assert.Equal((2, 4.5m), (two.RatingCount, two.RatingAvg));
        var listTwo = await JsonAsync(await ApiAsync((await ReviewedByAsync()).Target, HttpMethod.Get, $"/users/{target.Id}/reviews"));
        Assert.True(listTwo.GetProperty("isNewcomer").GetBoolean());
        Assert.Equal(JsonValueKind.Null, listTwo.GetProperty("ratingAvg").ValueKind);   // два отзыва ничего не говорят о человеке

        var third = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        var created = await ApiAsync(third, HttpMethod.Post, "/exchange-requests",
            new { toUserId = target.Id, toTeachesSkillId = world.S(1), fromTeachesSkillId = world.S(0), durationDays = 30 });
        var requestId = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var exchangeId = (await JsonAsync(await ApiAsync(target, HttpMethod.Post, $"/exchange-requests/{requestId}/accept"))).GetProperty("exchangeId").GetGuid();
        await CompletedSessionAsync(exchangeId, third.Id);
        await CloseAsync(exchangeId);
        await FinalizeAsync(third, exchangeId, [3, 4, 5, 4]);

        var three = await UserAsync(target.Id);
        Assert.Equal(3, three.RatingCount);
        Assert.Equal(4.33m, three.RatingAvg);   // (5 + 4 + 4) / 3, округлено до сотых
        var viewer = third;
        var list = await JsonAsync(await ApiAsync(viewer, HttpMethod.Get, $"/users/{target.Id}/reviews"));
        Assert.False(list.GetProperty("isNewcomer").GetBoolean());
        Assert.Equal(4.33m, list.GetProperty("ratingAvg").GetDecimal());
        Assert.Equal(3, list.GetProperty("ratingCount").GetInt32());
    }

    [Fact]
    public async Task Only_published_and_undisputed_reviews_count_a_frozen_hidden_or_pending_one_does_not()
    {
        var (target, authors, _) = await ReviewedByAsync([5, 5, 5, 5], [3, 3, 3, 3], [1, 1, 1, 1]);
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IReviewStore>();
        Assert.Equal(new RatingAggregate(3, 3m), await store.AggregateAsync(target.Id, CancellationToken.None));

        async Task SetStatus(Guid author, ReviewStatus status) => await _factory.QueryAsync(async db =>
        {
            await db.Reviews.Where(r => r.AuthorId == author && r.TargetId == target.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status));
            return 0;
        });

        await SetStatus(authors[2].Id, ReviewStatus.Frozen);   // спор: пока не считается
        Assert.Equal(new RatingAggregate(2, 4m), await store.AggregateAsync(target.Id, CancellationToken.None));
        await SetStatus(authors[1].Id, ReviewStatus.Hidden);
        Assert.Equal(new RatingAggregate(1, 5m), await store.AggregateAsync(target.Id, CancellationToken.None));
        await SetStatus(authors[0].Id, ReviewStatus.Pending);
        Assert.Equal(new RatingAggregate(0, 0m), await store.AggregateAsync(target.Id, CancellationToken.None));
    }

    // ---------- отзывы в профиле ----------

    [Fact]
    public async Task The_profile_lists_published_reviews_newest_first_without_hidden_and_pending_ones_and_pages_them()
    {
        var (target, authors, _) = await ReviewedByAsync([5, 5, 5, 5], [4, 4, 4, 4], [3, 3, 3, 3]);
        await _factory.QueryAsync(async db =>
        {
            await db.Reviews.Where(r => r.AuthorId == authors[1].Id && r.TargetId == target.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewStatus.Hidden));
            var oldest = await db.Reviews.Where(r => r.AuthorId == authors[0].Id && r.TargetId == target.Id).Select(r => r.Id).SingleAsync();
            await db.Reviews.Where(r => r.Id == oldest).ExecuteUpdateAsync(s => s.SetProperty(r => r.PublishedAt, Now.AddDays(-3)));
            return 0;
        });

        var list = await JsonAsync(await ApiAsync(authors[0], HttpMethod.Get, $"/users/{target.Id}/reviews"));

        Assert.Equal(2, list.GetProperty("totalCount").GetInt32());                                         // скрытый не виден
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(authors[2].Id, items[0].GetProperty("author").GetProperty("id").GetGuid());            // новые сверху
        Assert.Equal(authors[0].Id, items[1].GetProperty("author").GetProperty("id").GetGuid());
        Assert.All(items, i => Assert.False(i.TryGetProperty("status", out _)));                            // публичный отзыв без служебных полей
        Assert.All(items, i => Assert.False(i.TryGetProperty("targetId", out _)));

        var page = await JsonAsync(await ApiAsync(authors[0], HttpMethod.Get, $"/users/{target.Id}/reviews?page=2&pageSize=1"));
        Assert.Equal(authors[0].Id, page.GetProperty("items")[0].GetProperty("author").GetProperty("id").GetGuid());
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task A_frozen_review_stays_visible_in_the_profile_without_giving_away_the_dispute()
    {
        var (target, authors, _) = await ReviewedByAsync([2, 2, 2, 2]);
        await _factory.QueryAsync(async db =>
        {
            await db.Reviews.Where(r => r.TargetId == target.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewStatus.Frozen));
            return 0;
        });

        var list = await JsonAsync(await ApiAsync(authors[0], HttpMethod.Get, $"/users/{target.Id}/reviews"));

        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        var exchangeId = (await _factory.QueryAsync(db => db.Reviews.AsNoTracking().SingleAsync(r => r.TargetId == target.Id))).ExchangeId;
        var forTarget = await StateAsync(target, exchangeId);
        Assert.Equal("Published", forTarget.GetProperty("reviewAboutMe").GetProperty("status").GetString());   // адресат не видит «Frozen»
        Assert.Equal("Frozen", (await StateAsync(authors[0], exchangeId)).GetProperty("myReview").GetProperty("status").GetString());   // автор видит настоящий
    }

    [Fact]
    public async Task A_review_of_a_deleted_author_stays_with_the_deleted_user_label_and_keeps_counting()
    {
        var (target, authors, _) = await ReviewedByAsync([4, 4, 4, 4]);
        await SetStatusAsync(authors[0].Id, UserStatus.Deleted);
        var world = await MatchTestWorld.CreateAsync(_factory);
        var viewer = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));

        var list = await JsonAsync(await ApiAsync(viewer, HttpMethod.Get, $"/users/{target.Id}/reviews"));

        var author = list.GetProperty("items")[0].GetProperty("author");
        Assert.True(author.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(User.DeletedDisplayName, author.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, author.GetProperty("avatarUrl").ValueKind);
        Assert.Equal(1, (await UserAsync(target.Id)).RatingCount);   // отзыв продолжает влиять на рейтинг
    }

    [Fact]
    public async Task Reviews_of_a_person_are_visible_to_exactly_those_who_can_see_the_profile()
    {
        var (target, _, _) = await ReviewedByAsync([4, 4, 4, 4]);
        var world = await MatchTestWorld.CreateAsync(_factory);
        var viewer = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        var moderator = await ModerationTestData.StaffAsync(_factory, UserRole.Moderator);

        Assert.Equal(HttpStatusCode.OK, (await ApiAsync(viewer, HttpMethod.Get, $"/users/{target.Id}/reviews")).StatusCode);

        // Заблокировал — профиль и отзывы для пары не существуют (404, как у профиля).
        await ApiAsync(viewer, HttpMethod.Post, $"/users/{target.Id}/block");
        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(viewer, HttpMethod.Get, $"/users/{target.Id}/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(target, HttpMethod.Get, $"/users/{viewer.Id}/reviews")).StatusCode);

        // Скрытый из подбора и удалённый пользователи.
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsSearchable, false));
            return 0;
        });
        var other = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));
        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(other, HttpMethod.Get, $"/users/{target.Id}/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApiAsync(other, HttpMethod.Get, $"/users/{Guid.NewGuid()}/reviews")).StatusCode);

        var staff = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/users/{other.Id}/reviews");
        staff.Headers.Authorization = new AuthenticationHeaderValue("Bearer", moderator.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(staff)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync($"/api/v1/users/{other.Id}/reviews")).StatusCode);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=999")]
    public async Task A_bad_page_is_a_bad_request(string query)
    {
        var world = await MatchTestWorld.CreateAsync(_factory);
        var person = await world.UserAsync((0, SkillLevel.Beginner), (1, SkillLevel.Beginner));

        var response = await ApiAsync(person, HttpMethod.Get, $"/users/{person.Id}/reviews?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_paging", await CodeAsync(response));
    }

    // ---------- главная страница ----------

    private async Task<List<JsonElement>> WriteReviewActionsAsync(MatchPerson who)
    {
        var dashboard = await JsonAsync(await ApiAsync(who, HttpMethod.Get, "/me/dashboard"));
        return dashboard.GetProperty("actionsNeeded").EnumerateArray().Where(a => a.GetProperty("type").GetString() == "WriteReview").ToList();
    }

    [Fact]
    public async Task The_dashboard_asks_to_write_a_review_until_it_is_written_with_the_deadline_as_due_time()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();
        var closed = (await _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == exchangeId))).ClosedAt!.Value;

        var action = Assert.Single(await WriteReviewActionsAsync(a));
        Assert.Equal(exchangeId, action.GetProperty("exchangeId").GetGuid());
        Assert.Equal(closed.AddDays(14), action.GetProperty("dueAt").GetDateTime(), TimeSpan.FromSeconds(1));
        Assert.Single(await WriteReviewActionsAsync(b));

        await WriteAsync(a, exchangeId);

        Assert.Empty(await WriteReviewActionsAsync(a));     // написал: действие ушло
        Assert.Single(await WriteReviewActionsAsync(b));    // партнёр ещё должен
    }

    [Fact]
    public async Task The_dashboard_does_not_ask_when_there_is_no_right_or_the_deadline_passed()
    {
        var (_, a, b, noSessions) = await NewExchangeAsync();
        await CloseAsync(noSessions, ExchangeStatus.Aborted);
        var (_, c, d, expired) = await NewExchangeAsync();
        await CompletedSessionAsync(expired, c.Id);
        await CloseAsync(expired, closedAt: Now.AddDays(-15));
        var (_, e, f, deleted) = await NewExchangeAsync();
        await CompletedSessionAsync(deleted, e.Id);
        await CloseAsync(deleted);
        await SetStatusAsync(f.Id, UserStatus.Deleted);
        var (_, g, h, active) = await NewExchangeAsync();

        foreach (var who in new[] { a, b, c, d, e, g, h })
        {
            Assert.Empty(await WriteReviewActionsAsync(who));
        }
    }

    // ---------- приглашения и напоминания ----------

    [Fact]
    public async Task Closing_an_exchange_through_the_api_schedules_the_invitation_and_the_reminder()
    {
        var (_, a, b, exchangeId) = await NewExchangeAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<IBackgroundJobs>();
        var before = jobs.Scheduled.Count;

        Assert.Equal(HttpStatusCode.OK, (await ApiAsync(a, HttpMethod.Post, $"/exchanges/{exchangeId}/abort", new { reason = "Не получается по времени" })).StatusCode);

        var scheduled = jobs.Scheduled.Skip(before).ToList();
        var invite = Assert.Single(scheduled, j => j.Description.Contains(nameof(ReviewJobs.InviteAsync)));
        var remind = Assert.Single(scheduled, j => j.Description.Contains(nameof(ReviewJobs.RemindAsync)));
        Assert.Equal(Now.AddSeconds(30), invite.RunAt.UtcDateTime, TimeSpan.FromSeconds(5));
        var closed = (await _factory.QueryAsync(db => db.Exchanges.AsNoTracking().SingleAsync(e => e.Id == exchangeId))).ClosedAt!.Value;
        Assert.Equal(closed.AddDays(12), remind.RunAt.UtcDateTime, TimeSpan.FromSeconds(1));   // за два дня до срока
        _ = b;
    }

    [Fact]
    public async Task A_block_that_aborts_an_exchange_schedules_the_invitation_too()
    {
        var (_, blocker, blocked, exchangeId) = await NewExchangeAsync();
        var jobs = (InlineBackgroundJobs)_factory.Services.GetRequiredService<IBackgroundJobs>();
        var before = jobs.Scheduled.Count;

        await ApiAsync(blocker, HttpMethod.Post, $"/users/{blocked.Id}/block");

        Assert.Single(jobs.Scheduled.Skip(before), j => j.Description.Contains(nameof(ReviewJobs.InviteAsync)));
        _ = exchangeId;
    }

    [Fact]
    public async Task The_invitation_goes_to_each_participant_with_the_right_once_and_not_again()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();

        await RunJobAsync(j => j.InviteAsync(exchangeId));
        await RunJobAsync(j => j.InviteAsync(exchangeId));   // повтор ничего не шлёт

        foreach (var who in new[] { a, b })
        {
            var n = Assert.Single(await NotificationsAsync(who, "ReviewAvailable"));
            Assert.Equal(exchangeId, n.GetProperty("payload").GetProperty("exchangeId").GetGuid());
            Assert.Equal((who == a ? b : a).Id, n.GetProperty("payload").GetProperty("userId").GetGuid());
            Assert.True(n.GetProperty("payload").TryGetProperty("deadlineAt", out _));
        }
    }

    [Fact]
    public async Task The_invitation_skips_those_without_the_right_and_those_who_already_wrote()
    {
        var (_, a, b, exchangeId) = await NewExchangeAsync();
        await FinalNoShowsAsync(exchangeId, a.Id, culprit: b.Id, 3);
        await CloseAsync(exchangeId, ExchangeStatus.Aborted, abortedBy: b.Id);

        await RunJobAsync(j => j.InviteAsync(exchangeId));

        Assert.Single(await NotificationsAsync(a, "ReviewAvailable"));   // пострадавший приглашён
        Assert.Empty(await NotificationsAsync(b, "ReviewAvailable"));    // неявившийся нет

        var (_, c, d, none) = await NewExchangeAsync();
        await CloseAsync(none, ExchangeStatus.Aborted);
        await RunJobAsync(j => j.InviteAsync(none));
        Assert.Empty(await NotificationsAsync(c, "ReviewAvailable"));
        Assert.Empty(await NotificationsAsync(d, "ReviewAvailable"));

        var (_, e, f, wrote) = await ReadyAsync();
        await WriteAsync(e, wrote);
        await RunJobAsync(j => j.InviteAsync(wrote));
        Assert.Empty(await NotificationsAsync(e, "ReviewAvailable"));    // уже написал
        Assert.Single(await NotificationsAsync(f, "ReviewAvailable"));
    }

    [Fact]
    public async Task No_invitation_for_an_exchange_that_is_not_closed_or_does_not_exist()
    {
        var (_, a, _, active) = await NewExchangeAsync();

        await RunJobAsync(j => j.InviteAsync(active));
        await RunJobAsync(j => j.InviteAsync(Guid.NewGuid()));

        Assert.Empty(await NotificationsAsync(a, "ReviewAvailable"));
    }

    [Fact]
    public async Task The_reminder_goes_only_to_those_who_have_not_written_yet_and_only_once()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();
        await WriteAsync(a, exchangeId);

        await RunJobAsync(j => j.RemindAsync(exchangeId));
        await RunJobAsync(j => j.RemindAsync(exchangeId));

        Assert.Empty(await NotificationsAsync(a, "ReviewReminder"));
        Assert.Single(await NotificationsAsync(b, "ReviewReminder"));
    }

    // ---------- выгрузка данных ----------

    [Fact]
    public async Task The_data_export_has_my_written_reviews_even_unpublished_and_only_published_ones_about_me()
    {
        var (_, a, b, exchangeId) = await ReadyAsync();
        await WriteAsync(a, exchangeId, Body(5, 4, 3, 4, "Мой отзыв партнёру: всё хорошо, спасибо за уроки."));   // ещё не опубликован

        async Task<string> Export(Guid userId)
        {
            using var scope = _factory.Services.CreateScope();
            using var stream = new MemoryStream();
            await scope.ServiceProvider.GetRequiredService<IUserDataExportSource>().WriteAsync(userId, stream, Now, CancellationToken.None);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        using var forA = JsonDocument.Parse(await Export(a.Id));
        var written = forA.RootElement.GetProperty("reviewsWritten").EnumerateArray().Single();
        Assert.Equal("Pending", written.GetProperty("status").GetString());
        Assert.Equal(b.User.Name, written.GetProperty("about").GetString());
        Assert.Equal("Мой отзыв партнёру: всё хорошо, спасибо за уроки.", written.GetProperty("text").GetString());
        Assert.Empty(forA.RootElement.GetProperty("reviewsReceived").EnumerateArray());

        using var forB = JsonDocument.Parse(await Export(b.Id));
        Assert.Empty(forB.RootElement.GetProperty("reviewsReceived").EnumerateArray());   // слепой отзыв обо мне в выгрузке тоже скрыт
        Assert.Empty(forB.RootElement.GetProperty("reviewsWritten").EnumerateArray());

        await WriteAsync(b, exchangeId, Body(4, 4, 4, 4, "Хороший партнёр, договаривались чётко и вовремя."));   // оба опубликованы
        using var after = JsonDocument.Parse(await Export(b.Id));
        var received = after.RootElement.GetProperty("reviewsReceived").EnumerateArray().Single();
        Assert.Equal(a.User.Name, received.GetProperty("from").GetString());
        Assert.False(received.TryGetProperty("status", out _));
    }
}
