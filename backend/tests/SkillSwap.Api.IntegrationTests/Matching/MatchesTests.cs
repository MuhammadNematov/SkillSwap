using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SkillSwap.Api.IntegrationTests.Infrastructure;
using SkillSwap.Domain.Entities;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.IntegrationTests.Matching;

/// <summary>Подбор пар (раздел 5.3 ТЗ): GET /matches на настоящей БД.</summary>
[Collection(ApiCollection.Name)]
public class MatchesTests
{
    private const SkillLevel Beg = SkillLevel.Beginner;
    private const SkillLevel Mid = SkillLevel.Intermediate;
    private const SkillLevel Adv = SkillLevel.Advanced;

    private readonly ApiFactory _factory;

    public MatchesTests(ApiFactory factory) => _factory = factory;

    private static Guid[] Order(params MatchPerson[] people) => people.Select(p => p.Id).ToArray();

    private static async Task<string?> CodeAsync(HttpResponseMessage r) => await AuthApi.ReadCodeAsync(r);

    // ---------- доступ ----------

    [Fact]
    public async Task A_login_is_required()
    {
        var anonymous = await _factory.CreateClient().GetAsync("/api/v1/matches");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Without_a_complete_anketa_matching_is_closed_with_a_machine_readable_code()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var onlyTeach = await w.UserAsync([(0, Beg)], []);
        var onlyLearn = await w.UserAsync([], [(0, Beg)]);
        var empty = await w.UserAsync([], []);

        foreach (var person in new[] { onlyTeach, onlyLearn, empty })
        {
            var response = await w.GetRawAsync(person);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("skills_incomplete", await CodeAsync(response));
        }
    }

    [Fact]
    public async Task An_anketa_with_only_switched_off_skills_does_not_open_matching()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Beg), (1, Beg));
        await w.ModifyAsync(async db => (await db.Skills.SingleAsync(s => s.Id == w.S(0))).Update(w.CategoryId, "x", "x", "x" + w.S(0), null, false));

        var response = await w.GetRawAsync(me);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("skills_incomplete", await CodeAsync(response));
    }

    [Fact]
    public async Task Staff_and_blocked_accounts_cannot_use_matching()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Moderator);
        await w.ModifyAsync(db => { db.Users.Add(staff); return Task.CompletedTask; });
        var banned = await w.UserAsync((0, Beg), (1, Beg), new MatchTestWorld.Options(Status: UserStatus.Blocked));

        var staffReply = await w.GetRawAsync(new MatchPerson(staff, TestTokens.ForUser(_factory, staff)));
        var bannedReply = await w.GetRawAsync(banned);

        Assert.Equal(HttpStatusCode.Forbidden, staffReply.StatusCode);
        Assert.Equal("regular_account_required", await CodeAsync(staffReply));
        Assert.Equal(HttpStatusCode.Forbidden, bannedReply.StatusCode);
        Assert.Equal("account_not_active", await CodeAsync(bannedReply));
    }

    [Fact]
    public async Task A_user_who_is_hidden_can_still_search()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Beg), (1, Beg), new MatchTestWorld.Options(Searchable: false));
        var other = await w.UserAsync((1, Beg), (0, Beg));

        Assert.Equal([other.Id], await w.IdsAsync(me));
    }

    // ---------- полные и частичные ----------

    [Fact]
    public async Task Full_matches_come_first_then_partial_ones_and_each_carries_the_skills_that_matched()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));               // умею навык 0, хочу навык 1
        var full = await w.UserAsync((1, Mid), (0, Mid));             // учит 1 и хочет 0: полный
        var partial = await w.UserAsync((1, Mid), (2, Mid));          // учит 1, но хочет не то, что умею я: частичный

        var result = await w.GetAsync(me);

        var items = result.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(Order(full, partial), items.Select(i => i.GetProperty("userId").GetGuid()));
        Assert.Equal("Full", items[0].GetProperty("matchType").GetString());
        Assert.Equal("Partial", items[1].GetProperty("matchType").GetString());

        var theyTeach = items[0].GetProperty("theyTeach").EnumerateArray().Single();
        Assert.Equal(w.S(1), theyTeach.GetProperty("skillId").GetGuid());
        Assert.Equal(w.CategoryId, theyTeach.GetProperty("categoryId").GetGuid());
        Assert.Equal("Intermediate", theyTeach.GetProperty("theirLevel").GetString());
        Assert.Equal("Intermediate", theyTeach.GetProperty("myLevel").GetString());
        Assert.False(string.IsNullOrEmpty(theyTeach.GetProperty("nameEn").GetString()));
        Assert.Equal(w.S(0), items[0].GetProperty("theyLearn").EnumerateArray().Single().GetProperty("skillId").GetGuid());
        Assert.Equal(0, items[1].GetProperty("theyLearn").GetArrayLength());   // у частичного встречного интереса нет
        Assert.Equal(2, result.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task People_who_teach_nothing_i_want_are_not_candidates_even_if_they_want_what_i_teach()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));
        var wantsMyOnly = await w.UserAsync((3, Mid), (0, Mid));      // хочет моё, но учит не то
        var unrelated = await w.UserAsync((4, Mid), (5, Mid));
        var good = await w.UserAsync((1, Mid), (5, Mid));

        var ids = await w.IdsAsync(me);

        Assert.Equal([good.Id], ids);
        Assert.DoesNotContain(wantsMyOnly.Id, ids);
        Assert.DoesNotContain(unrelated.Id, ids);
    }

    [Fact]
    public async Task Several_matching_skills_make_one_entry_listing_all_of_them()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync([(0, Mid), (1, Mid)], [(2, Mid), (3, Mid)]);
        var partner = await w.UserAsync([(2, Mid), (3, Mid), (4, Mid)], [(0, Mid), (1, Mid)]);

        var result = await w.GetAsync(me);

        var item = result.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(1, result.GetProperty("totalCount").GetInt32());   // не по записи на пару навыков, а один человек
        Assert.Equal(partner.Id, item.GetProperty("userId").GetGuid());
        Assert.Equal(new[] { w.S(2), w.S(3) }.Order(), item.GetProperty("theyTeach").EnumerateArray().Select(s => s.GetProperty("skillId").GetGuid()).Order());
        Assert.Equal(new[] { w.S(0), w.S(1) }.Order(), item.GetProperty("theyLearn").EnumerateArray().Select(s => s.GetProperty("skillId").GetGuid()).Order());
        Assert.DoesNotContain(w.S(4), item.GetProperty("theyTeach").EnumerateArray().Select(s => s.GetProperty("skillId").GetGuid()));   // навык 4 мне не нужен
    }

    // ---------- уровни ----------

    [Fact]
    public async Task A_partner_must_teach_at_a_level_not_below_mine_and_a_higher_level_ranks_higher()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));               // хочу навык 1 на среднем уровне
        var tooLow = await w.UserAsync((1, Beg), (5, Mid));           // учит на начальном: не подходит
        var equal = await w.UserAsync((1, Mid), (5, Mid));
        var higher = await w.UserAsync((1, Adv), (5, Mid));

        var ids = await w.IdsAsync(me);

        Assert.Equal(Order(higher, equal), ids);                      // выше уровень — выше в выдаче
        Assert.DoesNotContain(tooLow.Id, ids);
    }

    [Fact]
    public async Task The_mirror_rule_my_level_must_not_be_below_the_partners_for_a_full_match()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Beg));               // я умею навык 0 на среднем
        var wantsAdvanced = await w.UserAsync((1, Beg), (0, Adv));    // хочет изучать на продвинутом: я не дотягиваю
        var wantsMid = await w.UserAsync((1, Beg), (0, Mid));         // на среднем: подходит
        var wantsBeginner = await w.UserAsync((1, Beg), (0, Beg));    // на начальном: подходит

        var result = await w.GetAsync(me);

        var byId = result.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("userId").GetGuid(), i => i.GetProperty("matchType").GetString());
        Assert.Equal("Partial", byId[wantsAdvanced.Id]);              // он остаётся в выдаче, но матч только частичный
        Assert.Equal("Full", byId[wantsMid.Id]);
        Assert.Equal("Full", byId[wantsBeginner.Id]);
    }

    // ---------- кого нет в выдаче ----------

    [Fact]
    public async Task Hidden_blocked_deleted_and_self_are_never_shown()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));
        var visible = await w.UserAsync((1, Mid), (0, Mid));
        var hidden = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(Searchable: false));
        var banned = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(Status: UserStatus.Blocked));
        var deleted = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(Status: UserStatus.Deleted));
        var iBlocked = await w.UserAsync((1, Mid), (0, Mid));
        var blockedMe = await w.UserAsync((1, Mid), (0, Mid));
        await w.ModifyAsync(db =>
        {
            db.UserBlocks.Add(UserBlock.Create(me.Id, iBlocked.Id, DateTime.UtcNow));
            db.UserBlocks.Add(UserBlock.Create(blockedMe.Id, me.Id, DateTime.UtcNow));
            return Task.CompletedTask;
        });

        var ids = await w.IdsAsync(me);

        Assert.Equal([visible.Id], ids);   // я, скрытый, заблокированный админом, удалённый и заблокированные в обе стороны отсутствуют
    }

    [Fact]
    public async Task Staff_accounts_are_never_candidates()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));
        var staff = User.CreateStaff(AuthApi.NewEmail(), "Staff", UserRole.Admin);
        await w.ModifyAsync(db =>
        {
            staff.IsSearchable = true;
            db.Users.Add(staff);
            db.UserSkills.Add(UserSkill.Create(staff.Id, w.S(1), SkillDirection.Teach, Mid, null));
            return Task.CompletedTask;
        });

        Assert.Empty(await w.IdsAsync(me));
    }

    [Fact]
    public async Task Skills_switched_off_by_the_admin_do_not_match_in_either_direction()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync([(0, Mid), (2, Mid)], [(1, Mid), (3, Mid)]);
        var viaOffTeach = await w.UserAsync((3, Mid), (5, Mid));      // его единственный нужный мне навык выключат
        var viaOffLearn = await w.UserAsync((1, Mid), (2, Mid));      // единственный общий интерес: навык 2, его выключат (но он учит 1)
        await w.ModifyAsync(async db =>
        {
            (await db.Skills.SingleAsync(s => s.Id == w.S(3))).Update(w.CategoryId, "x", "x", "x3" + w.S(3), null, false);
            (await db.Skills.SingleAsync(s => s.Id == w.S(2))).Update(w.CategoryId, "x", "x", "x2" + w.S(2), null, false);
        });

        var result = await w.GetAsync(me);

        var items = result.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([viaOffLearn.Id], items.Select(i => i.GetProperty("userId").GetGuid()));   // viaOffTeach учил выключенное
        Assert.Equal("Partial", items[0].GetProperty("matchType").GetString());               // а встречный интерес (навык 2) выключен
    }

    [Fact]
    public async Task A_switched_off_category_removes_its_skills_from_matching()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));
        await w.UserAsync((1, Mid), (0, Mid));
        await w.ModifyAsync(async db => (await db.SkillCategories.SingleAsync(c => c.Id == w.CategoryId)).Update("o", "o", "o" + w.CategoryId, 900, false));

        var response = await w.GetRawAsync(me);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);   // у меня не осталось включённых навыков
    }

    // ---------- порядок ----------

    [Fact]
    public async Task A_common_lesson_language_ranks_higher_and_is_reported()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid), new MatchTestWorld.Options(Languages: ["ru", "tg"]));
        var noCommon = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(Languages: ["de"]));
        var none = await w.UserAsync((1, Mid), (0, Mid));
        var common = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(Languages: ["tg", "en", "ru"]));

        var result = await w.GetAsync(me);

        var items = result.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(common.Id, items[0].GetProperty("userId").GetGuid());
        Assert.Equal(["ru", "tg"], items[0].GetProperty("commonLanguages").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(["en", "ru", "tg"], items[0].GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(0, items[1].GetProperty("commonLanguages").GetArrayLength());
        Assert.Contains(noCommon.Id, items.Select(i => i.GetProperty("userId").GetGuid()));
        Assert.Contains(none.Id, items.Select(i => i.GetProperty("userId").GetGuid()));
    }

    [Fact]
    public async Task A_nearer_time_zone_ranks_higher_and_the_difference_is_reported_in_hours()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid), new MatchTestWorld.Options(TimeZone: "Asia/Dushanbe"));   // UTC+5 круглый год
        var near = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(TimeZone: "Asia/Tashkent"));   // +5
        var mid = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(TimeZone: "Asia/Dhaka"));       // +6
        var far = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(TimeZone: "Asia/Tokyo"));       // +9

        var items = (await w.GetAsync(me)).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(Order(near, mid, far), items.Select(i => i.GetProperty("userId").GetGuid()));
        Assert.Equal([0.0, 1.0, 4.0], items.Select(i => i.GetProperty("timeZoneDifferenceHours").GetDouble()));
    }

    [Fact]
    public async Task A_higher_rating_ranks_higher_and_a_newcomer_has_no_average_and_ranks_last()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));
        var newcomer = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(RatingCount: 2, Rating: 5m));
        var low = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(RatingCount: 4, Rating: 3.2m));
        var high = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(RatingCount: 9, Rating: 4.8m));

        var items = (await w.GetAsync(me)).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(Order(high, low, newcomer), items.Select(i => i.GetProperty("userId").GetGuid()));
        Assert.Equal(4.8m, items[0].GetProperty("ratingAvg").GetDecimal());
        Assert.False(items[0].GetProperty("isNewcomer").GetBoolean());
        Assert.True(items[2].GetProperty("isNewcomer").GetBoolean());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("ratingAvg").ValueKind);   // «Новичок»: оценки нет
        Assert.Equal(2, items[2].GetProperty("ratingCount").GetInt32());
    }

    [Fact]
    public async Task Recent_activity_ranks_higher_when_everything_else_is_equal()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));
        var now = _factory.Clock.GetUtcNow().UtcDateTime;
        var never = await w.UserAsync((1, Mid), (0, Mid));
        var old = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(LastActiveAt: now - TimeSpan.FromDays(30)));
        var recent = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(LastActiveAt: now - TimeSpan.FromDays(2)));
        var justNow = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(LastActiveAt: now - TimeSpan.FromMinutes(5)));

        Assert.Equal(Order(justNow, recent, old, never), await w.IdsAsync(me));
    }

    [Fact]
    public async Task The_keys_apply_in_priority_order_in_the_real_query()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid), new MatchTestWorld.Options(Languages: ["ru"], TimeZone: "Asia/Dushanbe"));
        var partialPerfect = await w.UserAsync((1, Adv), (5, Mid), new MatchTestWorld.Options(Languages: ["ru"], TimeZone: "Asia/Dushanbe", RatingCount: 50, Rating: 5m));
        var fullFarNoLanguage = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(TimeZone: "America/Los_Angeles"));
        var fullWithLanguageFar = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(Languages: ["ru"], TimeZone: "America/Los_Angeles"));
        var fullWithLanguageNear = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(Languages: ["ru"], TimeZone: "Asia/Dushanbe"));

        Assert.Equal(Order(fullWithLanguageNear, fullWithLanguageFar, fullFarNoLanguage, partialPerfect), await w.IdsAsync(me));
    }

    // ---------- фильтры ----------

    [Fact]
    public async Task The_skill_filter_keeps_only_people_who_teach_that_skill()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync([(0, Mid)], [(1, Mid), (2, Mid)]);
        var teachesOne = await w.UserAsync((1, Mid), (0, Mid));
        var teachesTwo = await w.UserAsync((2, Mid), (0, Mid));
        var both = await w.UserAsync([(1, Mid), (2, Mid)], [(0, Mid)]);

        Assert.Equal(new[] { teachesOne.Id, both.Id }.Order(), (await w.IdsAsync(me, $"?skill={w.S(1)}")).Order());
        Assert.Equal(new[] { teachesTwo.Id, both.Id }.Order(), (await w.IdsAsync(me, $"?skill={w.S(2)}")).Order());
        Assert.Empty(await w.IdsAsync(me, $"?skill={w.S(4)}"));        // навыка нет в моём «Хочу изучать»
        Assert.Empty(await w.IdsAsync(me, $"?skill={Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await w.GetRawAsync(me, "?skill=not-a-guid")).StatusCode);
    }

    [Fact]
    public async Task The_language_filter_keeps_only_people_who_speak_it_and_accepts_any_case()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));
        var german = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(Languages: ["de", "en"]));
        var french = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(Languages: ["fr"]));
        await w.UserAsync((1, Mid), (0, Mid));   // без языков

        Assert.Equal([german.Id], await w.IdsAsync(me, "?lang=de"));
        Assert.Equal([german.Id], await w.IdsAsync(me, "?lang=DE"));
        Assert.Equal([french.Id], await w.IdsAsync(me, "?lang=fr"));
        Assert.Empty(await w.IdsAsync(me, "?lang=ja"));
    }

    [Fact]
    public async Task The_level_filter_keeps_only_partners_teaching_at_exactly_that_level()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Beg));
        var beginner = await w.UserAsync((1, Beg), (0, Mid));
        var middle = await w.UserAsync((1, Mid), (0, Mid));
        var advanced = await w.UserAsync((1, Adv), (0, Mid));

        Assert.Equal([beginner.Id], await w.IdsAsync(me, "?level=Beginner"));
        Assert.Equal([middle.Id], await w.IdsAsync(me, "?level=intermediate"));
        Assert.Equal([advanced.Id], await w.IdsAsync(me, "?level=ADVANCED"));
    }

    [Fact]
    public async Task The_minimum_rating_filter_drops_lower_rated_users_and_newcomers()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));
        var newcomer = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(RatingCount: 2, Rating: 5m));
        var low = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(RatingCount: 5, Rating: 3.0m));
        var good = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(RatingCount: 5, Rating: 4.5m));

        Assert.Equal([good.Id], await w.IdsAsync(me, "?minRating=4"));
        Assert.Equal([good.Id], await w.IdsAsync(me, "?minRating=4.5"));   // граница включается
        Assert.Equal(Order(good, low), await w.IdsAsync(me, "?minRating=3"));
        Assert.Empty(await w.IdsAsync(me, "?minRating=4.6"));
        Assert.Equal(3, (await w.IdsAsync(me, "?minRating=0")).Count);     // 0 — без фильтра: новички на месте
        Assert.Contains(newcomer.Id, await w.IdsAsync(me));
    }

    [Fact]
    public async Task Filters_combine_and_each_narrows_the_result()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync([(0, Mid)], [(1, Mid), (2, Mid)]);
        var target = await w.UserAsync((1, Adv), (0, Mid), new MatchTestWorld.Options(Languages: ["de"], RatingCount: 6, Rating: 4.7m));
        await w.UserAsync((1, Adv), (0, Mid), new MatchTestWorld.Options(Languages: ["fr"], RatingCount: 6, Rating: 4.7m));   // другой язык
        await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(Languages: ["de"], RatingCount: 6, Rating: 4.7m));   // другой уровень
        await w.UserAsync((2, Adv), (0, Mid), new MatchTestWorld.Options(Languages: ["de"], RatingCount: 6, Rating: 4.7m));   // другой навык
        await w.UserAsync((1, Adv), (0, Mid), new MatchTestWorld.Options(Languages: ["de"], RatingCount: 6, Rating: 3.0m));   // низкий рейтинг

        var ids = await w.IdsAsync(me, $"?skill={w.S(1)}&lang=de&level=Advanced&minRating=4.5");

        Assert.Equal([target.Id], ids);
    }

    [Theory]
    [InlineData("lang=xx", "invalid_language")]
    [InlineData("lang=eng", "invalid_language")]
    [InlineData("level=Expert", "invalid_level")]
    [InlineData("level=1", "invalid_level")]
    [InlineData("level=0", "invalid_level")]
    [InlineData("level=7", "invalid_level")]
    [InlineData("minRating=5.1", "invalid_min_rating")]
    [InlineData("minRating=-1", "invalid_min_rating")]
    [InlineData("page=0", "invalid_paging")]
    [InlineData("pageSize=0", "invalid_paging")]
    [InlineData("pageSize=51", "invalid_paging")]
    public async Task Bad_parameters_are_a_400_with_a_machine_readable_code(string query, string code)
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));

        var response = await w.GetRawAsync(me, "?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await CodeAsync(response));
    }

    [Fact]
    public async Task Filters_are_not_a_way_around_the_visibility_rules()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));
        var hidden = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(Searchable: false, Languages: ["de"]));

        Assert.Empty(await w.IdsAsync(me, $"?skill={w.S(1)}&lang=de"));
        Assert.DoesNotContain(hidden.Id, await w.IdsAsync(me, "?minRating=0"));
    }

    // ---------- страницы ----------

    [Fact]
    public async Task Pages_do_not_overlap_cover_everyone_and_report_the_total()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));
        var people = new List<MatchPerson>();
        for (var i = 0; i < 7; i++)
        {
            people.Add(await w.UserAsync((1, Mid), (0, Mid)));
        }

        var all = new List<Guid>();
        var sizes = new List<int>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await w.GetAsync(me, $"?pageSize=3&page={page}");
            Assert.Equal(7, result.GetProperty("totalCount").GetInt32());
            Assert.Equal(page, result.GetProperty("page").GetInt32());
            Assert.Equal(3, result.GetProperty("pageSize").GetInt32());
            var ids = result.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetGuid()).ToList();
            sizes.Add(ids.Count);
            all.AddRange(ids);
        }

        Assert.Equal([3, 3, 1], sizes);
        Assert.Equal(7, all.Distinct().Count());
        Assert.Equal(people.Select(p => p.Id).Order(), all.Order());
        Assert.Empty(await w.IdsAsync(me, "?pageSize=3&page=4"));   // за последней страницей пусто, а не ошибка
        Assert.Equal(all, await w.IdsAsync(me, "?pageSize=50"));    // порядок один и тот же при любом размере страницы
    }

    [Fact]
    public async Task No_candidates_is_an_empty_page_not_an_error()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));

        var result = await w.GetAsync(me);

        Assert.Equal(0, result.GetProperty("items").GetArrayLength());
        Assert.Equal(0, result.GetProperty("totalCount").GetInt32());
    }

    // ---------- что видно в карточке ----------

    [Fact]
    public async Task A_match_card_shows_no_private_data()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var me = await w.UserAsync((0, Mid), (1, Mid));
        await w.UserAsync((1, Mid), (0, Mid));

        var item = (await w.GetAsync(me)).GetProperty("items")[0];

        var keys = item.EnumerateObject().Select(p => p.Name).ToHashSet();
        foreach (var secret in new[] { "email", "dateOfBirth", "birthDate", "role", "bio", "lastActiveAt", "isSearchable", "uiLanguage", "status" })
        {
            Assert.DoesNotContain(secret, keys);
        }

        Assert.Contains("isMinor", keys);
        Assert.Contains("timeZone", keys);
    }

    // ---------- возрастные группы ----------

    private async Task WithSettingAsync(string? value, Func<Task> body)
    {
        var previous = await _factory.QueryAsync(db => db.SystemSettings.Where(s => s.Key == SystemSettingKeys.MinorsMatchOnlyMinors).Select(s => s.Value).SingleAsync());
        await SetSettingAsync(value ?? previous);
        try
        {
            await body();
        }
        finally
        {
            await SetSettingAsync(previous);
        }
    }

    private Task SetSettingAsync(string value) => _factory.QueryAsync(async db =>
    {
        var setting = await db.SystemSettings.SingleAsync(s => s.Key == SystemSettingKeys.MinorsMatchOnlyMinors);
        setting.ChangeValue(value);
        await db.SaveChangesAsync();
        return 0;
    });

    private DateOnly BornYearsAgo(int years) => DateOnly.FromDateTime(_factory.Clock.GetUtcNow().UtcDateTime).AddYears(-years).AddDays(-30);

    [Fact]
    public async Task By_default_teenagers_and_adults_do_not_match_each_other_and_the_card_says_who_is_a_teenager()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var teen = await w.UserAsync((0, Mid), (1, Mid), new MatchTestWorld.Options(BirthDate: BornYearsAgo(16)));
        var otherTeen = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(BirthDate: BornYearsAgo(17)));
        var adult = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(BirthDate: BornYearsAgo(18)));
        var olderAdult = await w.UserAsync((0, Mid), (1, Mid), new MatchTestWorld.Options(BirthDate: BornYearsAgo(40)));

        var forTeen = await w.GetAsync(teen);
        Assert.Equal([otherTeen.Id], forTeen.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetGuid()));
        Assert.True(forTeen.GetProperty("items")[0].GetProperty("isMinor").GetBoolean());

        var forAdult = await w.GetAsync(olderAdult);
        Assert.Equal([adult.Id], forAdult.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetGuid()));
        Assert.False(forAdult.GetProperty("items")[0].GetProperty("isMinor").GetBoolean());
    }

    [Fact]
    public async Task With_the_setting_switched_off_everyone_matches_everyone()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var teen = await w.UserAsync((0, Mid), (1, Mid), new MatchTestWorld.Options(BirthDate: BornYearsAgo(16)));
        var adult = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(BirthDate: BornYearsAgo(30)));

        await WithSettingAsync("false", async () =>
        {
            Assert.Equal([adult.Id], await w.IdsAsync(teen));
            Assert.Equal([teen.Id], await w.IdsAsync(adult));
        });

        Assert.Empty(await w.IdsAsync(teen));   // настройку вернули: группы снова раздельны
    }

    [Fact]
    public async Task A_missing_or_garbage_setting_falls_back_to_the_safe_default()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var teen = await w.UserAsync((0, Mid), (1, Mid), new MatchTestWorld.Options(BirthDate: BornYearsAgo(16)));
        await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(BirthDate: BornYearsAgo(30)));

        await WithSettingAsync("maybe", async () => Assert.Empty(await w.IdsAsync(teen)));   // непонятное значение = защита включена
    }

    [Fact]
    public async Task The_age_rule_also_hides_profiles_across_the_groups()
    {
        var w = await MatchTestWorld.CreateAsync(_factory);
        var teen = await w.UserAsync((0, Mid), (1, Mid), new MatchTestWorld.Options(BirthDate: BornYearsAgo(16)));
        var adult = await w.UserAsync((1, Mid), (0, Mid), new MatchTestWorld.Options(BirthDate: BornYearsAgo(30)));
        var client = _factory.CreateClient();

        async Task<HttpStatusCode> View(MatchPerson viewer, Guid id)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/users/{id}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer.Token);
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.NotFound, await View(adult, teen.Id));   // взрослый не видит подростка по прямой ссылке
        Assert.Equal(HttpStatusCode.NotFound, await View(teen, adult.Id));   // и наоборот
        Assert.Equal(HttpStatusCode.OK, await View(teen, teen.Id));          // себя видно всегда

        await WithSettingAsync("false", async () =>
        {
            Assert.Equal(HttpStatusCode.OK, await View(adult, teen.Id));
            Assert.Equal(HttpStatusCode.OK, await View(teen, adult.Id));
        });
    }
}
