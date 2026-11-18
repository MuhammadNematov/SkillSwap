using SkillSwap.Application.Matching;

namespace SkillSwap.Application.Tests.Matching;

public class MatchRankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);   // лето: у Москвы +3, у Душанбе +5, у Берлина +2

    private static int _seq;

    private static MatchCandidate C(
        bool full = false, bool lang = false, string tz = "Asia/Dushanbe", int level = 1,
        int ratingCount = 0, decimal rating = 0m, DateTime? lastActive = null) => new()
    {
        UserId = new Guid(++_seq, 0, 0, new byte[8]),
        IsFull = full,
        HasCommonLanguage = lang,
        TimeZone = tz,
        BestLevel = level,
        RatingCount = ratingCount,
        RatingAvg = rating,
        LastActiveAt = lastActive,
    };

    private static List<MatchCandidate> Order(string seekerTz, params MatchCandidate[] candidates) =>
        MatchRanking.Order(candidates, seekerTz, Now).Select(r => r.Candidate).ToList();

    [Fact]
    public void Full_matches_come_before_partial_ones_even_when_everything_else_favours_the_partial()
    {
        var partial = C(full: false, lang: true, tz: "Asia/Dushanbe", level: 3, ratingCount: 50, rating: 5m, lastActive: Now.UtcDateTime);
        var full = C(full: true, lang: false, tz: "Pacific/Auckland", level: 1);

        Assert.Equal([full, partial], Order("Asia/Dushanbe", partial, full));
    }

    [Fact]
    public void A_common_language_beats_a_nearer_time_zone()
    {
        var sameZoneNoLanguage = C(lang: false, tz: "Asia/Dushanbe");
        var farWithLanguage = C(lang: true, tz: "America/Los_Angeles");

        Assert.Equal([farWithLanguage, sameZoneNoLanguage], Order("Asia/Dushanbe", sameZoneNoLanguage, farWithLanguage));
    }

    [Fact]
    public void A_smaller_time_zone_difference_comes_first_and_is_measured_both_ways_around()
    {
        var same = C(tz: "Asia/Dushanbe");              // разница 0
        var moscow = C(tz: "Europe/Moscow");            // 2 часа (летом +3 против +5)
        var berlin = C(tz: "Europe/Berlin");            // 3 часа
        var tashkentIsSame = C(tz: "Asia/Tashkent");    // +5: тоже 0
        var tokyo = C(tz: "Asia/Tokyo");                // +9: 4 часа (в другую сторону)

        var order = Order("Asia/Dushanbe", tokyo, berlin, moscow, same, tashkentIsSame);

        Assert.Equal(new[] { same.UserId, tashkentIsSame.UserId }.Order(), order.Take(2).Select(c => c.UserId).Order());
        Assert.Equal([moscow, berlin, tokyo], order.Skip(2));
    }

    [Fact]
    public void The_difference_uses_daylight_saving_time_at_the_moment_of_the_request()
    {
        var berlin = C(tz: "Europe/Berlin");
        var moscow = C(tz: "Europe/Moscow");
        var winter = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);   // зимой Берлин +1, Москва +3 (без перехода)

        var summer = MatchRanking.Order([berlin, moscow], "Europe/Moscow", Now);        // Берлин +2 против +3: разница 1 час
        var inWinter = MatchRanking.Order([berlin, moscow], "Europe/Moscow", winter);   // Берлин +1 против +3: разница 2 часа

        Assert.Equal(60, summer.Single(r => r.Candidate == berlin).TimeZoneDifferenceMinutes);
        Assert.Equal(120, inWinter.Single(r => r.Candidate == berlin).TimeZoneDifferenceMinutes);
    }

    [Fact]
    public void An_unknown_time_zone_is_treated_as_utc_and_does_not_break_the_order()
    {
        var odd = C(tz: "Not/AZone");
        var utc = C(tz: "UTC");

        var result = MatchRanking.Order([odd, utc], "UTC", Now);

        Assert.All(result, r => Assert.Equal(0, r.TimeZoneDifferenceMinutes));
    }

    [Fact]
    public void A_higher_partner_level_comes_first_when_the_earlier_keys_are_equal()
    {
        var beginner = C(level: 1);
        var advanced = C(level: 3);
        var intermediate = C(level: 2);

        Assert.Equal([advanced, intermediate, beginner], Order("Asia/Dushanbe", beginner, advanced, intermediate));
    }

    [Fact]
    public void A_higher_rating_comes_first_and_a_newcomer_is_below_every_rated_user()
    {
        var newcomerWithHighStoredAverage = C(ratingCount: 2, rating: 5m);   // два отзыва ещё не рейтинг
        var low = C(ratingCount: 3, rating: 2.5m);
        var high = C(ratingCount: 30, rating: 4.9m);

        Assert.Equal([high, low, newcomerWithHighStoredAverage], Order("Asia/Dushanbe", newcomerWithHighStoredAverage, low, high));
    }

    [Fact]
    public void Recent_activity_decides_after_the_rating_and_a_week_is_the_border()
    {
        var weekAgoPlusMinute = C(lastActive: Now.UtcDateTime - TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1));
        var weekAgoMinusMinute = C(lastActive: Now.UtcDateTime - TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        var never = C(lastActive: null);
        var today = C(lastActive: Now.UtcDateTime);

        var order = Order("Asia/Dushanbe", never, weekAgoMinusMinute, weekAgoPlusMinute, today);

        Assert.Equal([today, weekAgoPlusMinute, weekAgoMinusMinute, never], order);   // внутри недели — позже активен выше
    }

    [Fact]
    public void Every_key_only_matters_when_the_previous_ones_are_equal()
    {
        // Два кандидата: у первого лучше только последний ключ (активность), у второго — рейтинг. Рейтинг важнее.
        var active = C(ratingCount: 5, rating: 3m, lastActive: Now.UtcDateTime);
        var rated = C(ratingCount: 5, rating: 4m, lastActive: Now.UtcDateTime - TimeSpan.FromDays(30));

        Assert.Equal([rated, active], Order("Asia/Dushanbe", active, rated));
    }

    [Fact]
    public void Full_ordering_is_deterministic_for_identical_candidates()
    {
        var a = C();
        var b = C();
        var c = C();

        var forward = Order("UTC", a, b, c).Select(x => x.UserId).ToList();
        var backward = Order("UTC", c, b, a).Select(x => x.UserId).ToList();

        Assert.Equal(forward, backward);   // порядок страниц не зависит от порядка, в котором БД вернула строки
    }

    [Fact]
    public void An_empty_input_gives_an_empty_result() => Assert.Empty(MatchRanking.Order([], "UTC", Now));
}
