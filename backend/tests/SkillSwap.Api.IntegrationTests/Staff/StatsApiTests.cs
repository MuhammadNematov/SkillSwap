using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Staff;

/// <summary>Статистика: полная для админа и суперадмина, личная для модератора; только агрегаты, периоды, форма ответа, время реакции и просрочки.</summary>
[Collection(ApiCollection.Name)]
public class StatsApiTests
{
    private const string Complaint = "Собеседник регулярно оскорбляет меня в чате и срывает договорённости.";

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public StatsApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private DateTime Now => _factory.Clock.GetUtcNow().UtcDateTime;

    private async Task<HttpResponseMessage> SendAsync(string? token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1" + url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> CodeAsync(HttpResponseMessage response) => (await JsonAsync(response)).GetProperty("code").GetString()!;

    /// <summary>Записи ниже пишутся по настоящим часам, а приложение в тестах живёт по замороженным: берём период с запасом вперёд, чтобы свежие записи в него попали.</summary>
    private static string Wide => $"?from={Iso(DateTime.UtcNow.AddDays(-30))}&to={Iso(DateTime.UtcNow.AddDays(1))}";

    private static string Iso(DateTime value) => Uri.EscapeDataString(value.ToString("o"));

    private Task<StaffPerson> StaffAsync(UserRole role) => ModerationTestData.StaffAsync(_factory, role);

    private async Task<JsonElement> AdminStatsAsync(StaffPerson admin, string query = "")
    {
        var response = await SendAsync(admin.Token, "/admin/stats" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    /// <summary>Обращение (жалоба A на B) с заданными временем подачи и сроком реакции; время ставим прямо в базе.</summary>
    private async Task<Guid> CaseAtAsync(DateTime createdAt, TimeSpan sla)
    {
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var created = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases")
        {
            Content = JsonContent.Create(new { type = "Conduct", category = "Insults", exchangeId = x.ExchangeId, description = Complaint }),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", x.A.Token) },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.CreatedAt, createdAt).SetProperty(c => c.DueAt, createdAt + sla));
            return 0;
        });
        return id;
    }

    private Task AuditAsync(Guid? actor, string action, Guid? target, DateTime at, string targetType = "Case") => _factory.QueryAsync(async db =>
    {
        db.AuditLogs.Add(AuditLog.Create(actor, action, targetType, target, "{}", null, at));
        await db.SaveChangesAsync();
        return 0;
    });

    // ======================= доступ =======================

    [Fact]
    public async Task The_full_statistics_is_for_admins_and_the_personal_one_for_moderators_only()
    {
        var admin = await StaffAsync(UserRole.Admin);
        var superAdmin = await StaffAsync(UserRole.SuperAdmin);
        var moderator = await StaffAsync(UserRole.Moderator);
        var (_, personal) = await StaffActor.PersonalAsync(_factory);
        var removed = await StaffAsync(UserRole.Admin);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == removed.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Status, UserStatus.Blocked));
            return 0;
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, "/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(null, "/moderation/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(personal, "/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(personal, "/moderation/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(moderator.Token, "/admin/stats")).StatusCode);       // модератору чужая статистика закрыта
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(admin.Token, "/moderation/stats")).StatusCode);      // у админа личной нет
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(superAdmin.Token, "/moderation/stats")).StatusCode);
        Assert.Equal("staff_not_active", await CodeAsync(await SendAsync(removed.Token, "/admin/stats")));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, "/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(superAdmin.Token, "/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(moderator.Token, "/moderation/stats")).StatusCode);
    }

    // ======================= период =======================

    [Fact]
    public async Task The_period_defaults_to_the_last_thirty_days_and_is_validated()
    {
        var admin = await StaffAsync(UserRole.Admin);

        var stats = await AdminStatsAsync(admin);

        var period = stats.GetProperty("period");
        Assert.Equal(Now, period.GetProperty("to").GetDateTime().ToUniversalTime(), TimeSpan.FromSeconds(2));
        Assert.Equal(Now.AddDays(-30), period.GetProperty("from").GetDateTime().ToUniversalTime(), TimeSpan.FromSeconds(2));
        var fromOnly = await AdminStatsAsync(admin, $"?from={Iso(Now.AddDays(-7))}");
        Assert.Equal(Now.AddDays(-7), fromOnly.GetProperty("period").GetProperty("from").GetDateTime().ToUniversalTime(), TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(admin.Token, $"/admin/stats?from={Iso(Now.AddDays(-366))}&to={Iso(Now)}")).StatusCode);   // ровно предел

        var backwards = await SendAsync(admin.Token, $"/admin/stats?from={Iso(Now)}&to={Iso(Now.AddDays(-1))}");
        var empty = await SendAsync(admin.Token, $"/admin/stats?from={Iso(Now)}&to={Iso(Now)}");
        var tooLong = await SendAsync(admin.Token, $"/admin/stats?from={Iso(Now.AddDays(-367))}&to={Iso(Now)}");
        Assert.Equal("invalid_period", await CodeAsync(backwards));
        Assert.Equal("invalid_period", await CodeAsync(empty));
        Assert.Equal("period_too_long", await CodeAsync(tooLong));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(admin.Token, "/admin/stats?from=вчера")).StatusCode);
        Assert.Equal("invalid_period", await CodeAsync(await SendAsync((await StaffAsync(UserRole.Moderator)).Token, $"/moderation/stats?from={Iso(Now)}&to={Iso(Now.AddDays(-1))}")));
    }

    // ======================= форма и персональные данные =======================

    private static void CollectNames(JsonElement element, List<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                names.Add(property.Name);
                CollectNames(property.Value, names);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectNames(item, names);
            }
        }
    }

    [Fact]
    public async Task The_answers_are_aggregates_only_and_dictionaries_list_every_value()
    {
        var admin = await StaffAsync(UserRole.Admin);
        var moderator = await StaffAsync(UserRole.Moderator);
        var caseId = await CaseAtAsync(Now.AddHours(-2), TimeSpan.FromHours(24));
        await AuditAsync(moderator.Id, "CaseTaken", caseId, Now.AddHours(-1));

        var adminJson = await SendAsync(admin.Token, "/admin/stats");
        var moderatorJson = await SendAsync(moderator.Token, "/moderation/stats");
        var adminText = await adminJson.Content.ReadAsStringAsync();
        var moderatorText = await moderatorJson.Content.ReadAsStringAsync();

        foreach (var text in new[] { adminText, moderatorText })
        {
            var names = new List<string>();
            CollectNames(JsonDocument.Parse(text).RootElement, names);
            Assert.DoesNotContain(names, n => System.Text.RegularExpressions.Regex.IsMatch(n, "(?i)email|name|ip(Address)?$|userid|actor|moderatorid|caseid|exchangeid|^id$"));
            Assert.DoesNotContain(moderator.User.Email!, text);
            Assert.DoesNotContain(moderator.Id.ToString(), text);
            Assert.DoesNotContain(caseId.ToString(), text);
        }

        var stats = JsonDocument.Parse(adminText).RootElement;
        var cases = stats.GetProperty("cases");
        Assert.Equal(Enum.GetNames<CaseType>().Order(), cases.GetProperty("openedByType").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(Enum.GetNames<CaseDecision>().Order(), cases.GetProperty("resolvedByDecision").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(Enum.GetNames<CaseStatus>().Order(), cases.GetProperty("backlog").EnumerateObject().Select(p => p.Name).Order());
        var mine = JsonDocument.Parse(moderatorText).RootElement;
        Assert.Equal(Enum.GetNames<CaseDecision>().Order(), mine.GetProperty("resolvedByDecision").EnumerateObject().Select(p => p.Name).Order());
    }

    // ======================= счётчики платформы =======================

    [Fact]
    public async Task Users_exchanges_lessons_and_cases_move_the_counters_as_expected()
    {
        var admin = await StaffAsync(UserRole.Admin);
        var before = await AdminStatsAsync(admin, Wide);

        var (blockedUser, _) = await StaffActor.PersonalAsync(_factory);
        await StaffActor.PersonalAsync(_factory);
        await _factory.QueryAsync(async db =>
        {
            await db.Users.Where(u => u.Id == blockedUser.Id).ExecuteUpdateAsync(x => x.SetProperty(u => u.Status, UserStatus.Blocked));
            return 0;
        });
        await StaffAsync(UserRole.Moderator);
        var x = await ModerationTestData.ExchangeAsync(_factory, _client);
        var lessons = new[] { SessionStatus.Completed, SessionStatus.Completed, SessionStatus.NoShow, SessionStatus.Missed, SessionStatus.Cancelled, SessionStatus.LateCancelled, SessionStatus.Unconfirmed, SessionStatus.NotCounted, SessionStatus.Scheduled };
        await _factory.QueryAsync(async db =>
        {
            foreach (var status in lessons)
            {
                var lesson = LessonSession.Propose(x.ExchangeId, x.A.Id, x.A.Id, Now.AddDays(2), null, Now, Now.AddDays(30));
                db.Add(lesson);
                await db.SaveChangesAsync();
                await db.Sessions.Where(s => s.Id == lesson.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, status).SetProperty(l => l.StartAt, Now.AddDays(-1)).SetProperty(l => l.EndAt, Now.AddDays(-1).AddMinutes(60)));
            }

            return 0;
        });
        var caseId = await CaseAtAsync(Now.AddHours(-1), TimeSpan.FromHours(72));

        var after = await AdminStatsAsync(admin, Wide);

        int Delta(JsonElement a, JsonElement b, params string[] path)
        {
            foreach (var step in path)
            {
                a = a.GetProperty(step);
                b = b.GetProperty(step);
            }

            return b.GetInt32() - a.GetInt32();
        }

        // Новых обычных аккаунтов шесть: два личных и по два участника у двух обменов (второй обмен создала жалоба); один из шести заблокирован.
        Assert.Equal(5, Delta(before, after, "users", "active"));
        Assert.Equal(1, Delta(before, after, "users", "blocked"));
        Assert.Equal(6, Delta(before, after, "users", "newInPeriod"));
        Assert.Equal(1, Delta(before, after, "users", "moderators"));
        Assert.Equal(2, Delta(before, after, "exchanges", "active"));
        Assert.Equal(2, Delta(before, after, "exchanges", "createdInPeriod"));
        Assert.Equal(2, Delta(before, after, "lessons", "completed"));
        Assert.Equal(1, Delta(before, after, "lessons", "noShow"));
        Assert.Equal(1, Delta(before, after, "lessons", "missed"));
        Assert.Equal(2, Delta(before, after, "lessons", "cancelled"));
        Assert.Equal(2, Delta(before, after, "lessons", "unconfirmed"));
        Assert.Equal(1, Delta(before, after, "cases", "opened"));
        Assert.Equal(1, Delta(before, after, "cases", "openedByType", "Conduct"));
        Assert.Equal(1, Delta(before, after, "cases", "backlog", "Open"));
        Assert.NotEqual(Guid.Empty, caseId);
    }

    [Fact]
    public async Task Resolved_cases_and_decisions_are_counted_by_the_day_of_the_decision()
    {
        var admin = await StaffAsync(UserRole.Admin);
        var moderator = await StaffAsync(UserRole.Moderator);
        var before = await AdminStatsAsync(admin, Wide);
        var caseId = await CaseAtAsync(Now.AddHours(-3), TimeSpan.FromHours(24));
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/moderation/cases/{caseId}/take") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", moderator.Token) } })).StatusCode);
        var resolve = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/v1/moderation/cases/{caseId}/resolve")
        {
            Content = JsonContent.Create(new { decision = "NoViolation", alsoWarned = false, reason = "Переписку изучил: нарушения нет, жалоба необоснованна." }),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", moderator.Token) },
        });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);

        var after = await AdminStatsAsync(admin, Wide);
        var mine = await JsonAsync(await SendAsync(moderator.Token, "/moderation/stats" + Wide));

        Assert.Equal(1, after.GetProperty("cases").GetProperty("resolved").GetInt32() - before.GetProperty("cases").GetProperty("resolved").GetInt32());
        Assert.Equal(1, after.GetProperty("cases").GetProperty("resolvedByDecision").GetProperty("NoViolation").GetInt32() - before.GetProperty("cases").GetProperty("resolvedByDecision").GetProperty("NoViolation").GetInt32());
        Assert.Equal(1, mine.GetProperty("taken").GetInt32());
        Assert.Equal(1, mine.GetProperty("resolved").GetInt32());
        Assert.Equal(1, mine.GetProperty("resolvedByDecision").GetProperty("NoViolation").GetInt32());
        Assert.Equal(0, mine.GetProperty("inWorkNow").GetInt32());
    }

    // ======================= реакция и просрочки (точные значения) =======================

    [Fact]
    public async Task The_reaction_time_is_measured_to_the_first_take_and_the_sla_share_is_counted()
    {
        var admin = await StaffAsync(UserRole.Admin);
        var first = await StaffAsync(UserRole.Moderator);
        var second = await StaffAsync(UserRole.Moderator);
        var created = Now.AddDays(-150).AddMinutes(Random.Shared.Next(0, 5000));   // своё узкое окно: чужих обращений в нём нет
        var quick = await CaseAtAsync(created, TimeSpan.FromHours(24));
        var slow = await CaseAtAsync(created.AddSeconds(30), TimeSpan.FromHours(24));
        var untouched = await CaseAtAsync(created.AddSeconds(40), TimeSpan.FromHours(24));
        await AuditAsync(first.Id, "CaseTaken", quick, created.AddHours(5));       // 5 часов: в срок
        await AuditAsync(second.Id, "CaseTaken", quick, created.AddHours(50));     // позднее взятие первым не считается
        await AuditAsync(second.Id, "CaseTaken", slow, created.AddHours(30));      // 30 часов: после срока

        var stats = await AdminStatsAsync(admin, $"?from={Iso(created.AddSeconds(-5))}&to={Iso(created.AddSeconds(60))}");

        var cases = stats.GetProperty("cases");
        Assert.Equal(3, cases.GetProperty("opened").GetInt32());
        var reaction = cases.GetProperty("reaction");
        Assert.Equal(2, reaction.GetProperty("casesTaken").GetInt32());   // нетронутое обращение не считается
        Assert.Equal(17.5, reaction.GetProperty("averageHours").GetDouble(), 0.01);
        Assert.Equal(17.5, reaction.GetProperty("medianHours").GetDouble(), 0.01);
        Assert.Equal(50.0, reaction.GetProperty("withinSlaPercent").GetDouble(), 0.01);
        Assert.NotEqual(Guid.Empty, untouched);

        var empty = await AdminStatsAsync(admin, $"?from={Iso(created.AddDays(-20))}&to={Iso(created.AddDays(-19))}");
        Assert.Equal(0, empty.GetProperty("cases").GetProperty("reaction").GetProperty("casesTaken").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("cases").GetProperty("reaction").GetProperty("averageHours").ValueKind);
    }

    [Fact]
    public async Task Overdue_cases_are_counted_now_and_by_the_day_they_were_marked()
    {
        var admin = await StaffAsync(UserRole.Admin);
        var before = await AdminStatsAsync(admin);
        var late = await CaseAtAsync(Now.AddDays(-5), TimeSpan.FromHours(24));          // не взято, срок вышел
        var marked = Now.AddHours(-12);
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(c => c.Id == late).ExecuteUpdateAsync(s => s.SetProperty(c => c.OverdueMarkedAt, marked));
            return 0;
        });
        await CaseAtAsync(Now.AddHours(-1), TimeSpan.FromHours(24));                   // ещё в срок

        var after = await AdminStatsAsync(admin);

        Assert.Equal(1, after.GetProperty("cases").GetProperty("overdueNow").GetInt32() - before.GetProperty("cases").GetProperty("overdueNow").GetInt32());
        Assert.Equal(1, after.GetProperty("cases").GetProperty("overdueMarkedInPeriod").GetInt32() - before.GetProperty("cases").GetProperty("overdueMarkedInPeriod").GetInt32());
        var around = await AdminStatsAsync(admin, $"?from={Iso(marked.AddMinutes(-1))}&to={Iso(marked.AddMinutes(1))}");   // окно ровно вокруг отметки
        var elsewhere = await AdminStatsAsync(admin, $"?from={Iso(marked.AddDays(-3))}&to={Iso(marked.AddDays(-2))}");
        Assert.Equal(1, around.GetProperty("cases").GetProperty("overdueMarkedInPeriod").GetInt32());
        Assert.Equal(0, elsewhere.GetProperty("cases").GetProperty("overdueMarkedInPeriod").GetInt32());
    }

    // ======================= личная статистика модератора =======================

    [Fact]
    public async Task A_moderator_sees_only_their_own_work_with_the_exact_numbers()
    {
        var me = await StaffAsync(UserRole.Moderator);
        var colleague = await StaffAsync(UserRole.Moderator);
        var t = Now.AddDays(-2);
        // Два обращения, которые я взял первым: реакция 5 и 30 часов, срок 24 часа.
        var a = await CaseAtAsync(t.AddHours(-5), TimeSpan.FromHours(24));
        var b = await CaseAtAsync(t.AddHours(-30), TimeSpan.FromHours(24));
        // Обращение коллеги, которое я взял повторно: взятие считается, реакция нет.
        var c = await CaseAtAsync(t.AddHours(-40), TimeSpan.FromHours(24));
        await AuditAsync(me.Id, "CaseTaken", a, t);
        await AuditAsync(me.Id, "CaseTaken", b, t);
        await AuditAsync(colleague.Id, "CaseTaken", c, t.AddHours(-30));
        await AuditAsync(me.Id, "CaseTaken", c, t.AddHours(1));
        await AuditAsync(colleague.Id, "CaseTaken", a, t.AddHours(2));    // чужое взятие моей статистики не меняет
        await AuditAsync(me.Id, "AppealConcluded", a, t.AddHours(3));
        await AuditAsync(me.Id, "CaseTaken", Guid.NewGuid(), Now.AddDays(-60));   // вне периода
        await _factory.QueryAsync(async db =>
        {
            await db.Cases.Where(x => x.Id == a).ExecuteUpdateAsync(s => s.SetProperty(x => x.ModeratorId, me.Id));
            await db.Cases.Where(x => x.Id == b).ExecuteUpdateAsync(s => s.SetProperty(x => x.ModeratorId, me.Id));
            await db.Cases.Where(x => x.Id == c).ExecuteUpdateAsync(s => s.SetProperty(x => x.ModeratorId, me.Id));
            return 0;
        });
        await SetCaseStateAsync(a, CaseStatus.Resolved, CaseDecision.NoViolation, t.AddHours(4));
        await SetCaseStateAsync(b, CaseStatus.Closed, CaseDecision.Warning, t.AddHours(5));
        await SetCaseStateAsync(c, CaseStatus.WaitingResponse, null, null);
        var extra = await CaseAtAsync(Now.AddHours(-1), TimeSpan.FromHours(24));
        await SetCaseStateAsync(extra, CaseStatus.InProgress, null, null, me.Id);

        var stats = await JsonAsync(await SendAsync(me.Token, "/moderation/stats"));
        var others = await JsonAsync(await SendAsync(colleague.Token, "/moderation/stats"));

        Assert.Equal(3, stats.GetProperty("taken").GetInt32());
        Assert.Equal(2, stats.GetProperty("resolved").GetInt32());
        Assert.Equal(1, stats.GetProperty("resolvedByDecision").GetProperty("NoViolation").GetInt32());
        Assert.Equal(1, stats.GetProperty("resolvedByDecision").GetProperty("Warning").GetInt32());
        Assert.Equal(1, stats.GetProperty("appealReviewsConcluded").GetInt32());
        Assert.Equal(2, stats.GetProperty("inWorkNow").GetInt32());   // ждёт ответов и в работе
        var reaction = stats.GetProperty("reaction");
        Assert.Equal(2, reaction.GetProperty("casesTaken").GetInt32());
        Assert.Equal(17.5, reaction.GetProperty("averageHours").GetDouble(), 0.01);
        Assert.Equal(17.5, reaction.GetProperty("medianHours").GetDouble(), 0.01);
        Assert.Equal(50.0, reaction.GetProperty("withinSlaPercent").GetDouble(), 0.01);
        Assert.Equal(2, others.GetProperty("taken").GetInt32());     // у коллеги свои цифры (два взятия: моё обращение a и c)
        Assert.Equal(0, others.GetProperty("resolved").GetInt32());
    }

    private Task SetCaseStateAsync(Guid id, CaseStatus status, CaseDecision? decision, DateTime? resolvedAt, Guid? moderator = null) => _factory.QueryAsync(async db =>
    {
        await db.Cases.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Status, status).SetProperty(c => c.Decision, decision).SetProperty(c => c.ResolvedAt, resolvedAt)
            .SetProperty(c => c.ModeratorId, c => moderator ?? c.ModeratorId));
        return 0;
    });
}
