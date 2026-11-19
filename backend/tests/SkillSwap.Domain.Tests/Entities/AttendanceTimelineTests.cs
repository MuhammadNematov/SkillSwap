using SkillSwap.Domain.Entities;

namespace SkillSwap.Domain.Tests.Entities;

/// <summary>Присутствие, собранное из журнала событий LiveKit: порядок доставки, повторы и пропуски не должны его портить (раздел 6.3 ТЗ).</summary>
public class AttendanceTimelineTests
{
    private static readonly DateTime T0 = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
    private const string Room = "session-x";
    private const string Alice = "11111111-1111-1111-1111-111111111111";
    private const string Bob = "22222222-2222-2222-2222-222222222222";

    private static int _seq;

    private static LiveKitWebhookEvent Ev(string type, string? identity, int minute, int second = 0)
    {
        var e = LiveKitWebhookEvent.Create($"EV{++_seq}", type, Room, identity, T0.AddMinutes(minute).AddSeconds(second));
        e.CreatedAt = T0.AddHours(1).AddSeconds(_seq);   // порядок приёма
        return e;
    }

    private static LiveKitWebhookEvent Join(string who, int minute, int second = 0) => Ev("participant_joined", who, minute, second);
    private static LiveKitWebhookEvent Leave(string who, int minute, int second = 0) => Ev("participant_left", who, minute, second);

    [Fact]
    public void A_join_without_a_leave_is_an_open_interval()
    {
        var spans = AttendanceTimeline.Build([Join(Alice, 1)], Alice);

        Assert.Equal([new AttendanceSpan(T0.AddMinutes(1), null)], spans);
    }

    [Fact]
    public void Reconnects_give_several_intervals()
    {
        var spans = AttendanceTimeline.Build([Join(Alice, 0), Leave(Alice, 10), Join(Alice, 12), Leave(Alice, 30), Join(Alice, 31)], Alice);

        Assert.Equal(
            [
                new AttendanceSpan(T0, T0.AddMinutes(10)),
                new AttendanceSpan(T0.AddMinutes(12), T0.AddMinutes(30)),
                new AttendanceSpan(T0.AddMinutes(31), null),
            ],
            spans);
    }

    [Fact]
    public void Events_of_the_other_participant_are_not_mixed_in()
    {
        var events = new[] { Join(Alice, 0), Join(Bob, 5), Leave(Bob, 8), Leave(Alice, 20) };

        Assert.Equal([new AttendanceSpan(T0, T0.AddMinutes(20))], AttendanceTimeline.Build(events, Alice));
        Assert.Equal([new AttendanceSpan(T0.AddMinutes(5), T0.AddMinutes(8))], AttendanceTimeline.Build(events, Bob));
    }

    [Fact]
    public void The_order_of_delivery_does_not_matter()
    {
        var inOrder = new[] { Join(Alice, 0), Leave(Alice, 10), Join(Alice, 12), Leave(Alice, 30) };
        var shuffled = new[] { inOrder[3], inOrder[1], inOrder[0], inOrder[2] };

        Assert.Equal(AttendanceTimeline.Build(inOrder, Alice), AttendanceTimeline.Build(shuffled, Alice));
    }

    [Fact]
    public void A_repeated_join_and_a_repeated_leave_change_nothing()
    {
        var spans = AttendanceTimeline.Build([Join(Alice, 0), Join(Alice, 3), Leave(Alice, 10), Leave(Alice, 11)], Alice);

        Assert.Equal([new AttendanceSpan(T0, T0.AddMinutes(10))], spans);
    }

    [Fact]
    public void A_leave_without_a_join_is_ignored()
    {
        Assert.Empty(AttendanceTimeline.Build([Leave(Alice, 5)], Alice));
    }

    [Fact]
    public void The_identity_is_compared_without_regard_to_case()
    {
        var spans = AttendanceTimeline.Build([Join(Alice.ToUpperInvariant(), 0), Leave(Alice, 4)], Alice);

        Assert.Equal([new AttendanceSpan(T0, T0.AddMinutes(4))], spans);
    }

    [Fact]
    public void A_finished_room_closes_everyones_open_interval()
    {
        var events = new[] { Join(Alice, 0), Join(Bob, 2), Ev("room_finished", null, 50) };

        Assert.Equal([new AttendanceSpan(T0, T0.AddMinutes(50))], AttendanceTimeline.Build(events, Alice));
        Assert.Equal([new AttendanceSpan(T0.AddMinutes(2), T0.AddMinutes(50))], AttendanceTimeline.Build(events, Bob));
    }

    [Fact]
    public void A_join_and_a_leave_in_the_same_second_keep_the_order_of_arrival()
    {
        // Выход и повторный вход в одну секунду: по порядку приёма это «вышел, зашёл» — второй интервал не теряется.
        var spans = AttendanceTimeline.Build([Join(Alice, 0), Leave(Alice, 5), Join(Alice, 5)], Alice);

        Assert.Equal([new AttendanceSpan(T0, T0.AddMinutes(5)), new AttendanceSpan(T0.AddMinutes(5), null)], spans);
    }

    [Fact]
    public void A_join_and_a_leave_with_identical_time_and_arrival_count_the_join_first()
    {
        var join = Join(Alice, 5);
        var leave = Leave(Alice, 5);
        leave.CreatedAt = join.CreatedAt;

        Assert.Equal([new AttendanceSpan(T0.AddMinutes(5), T0.AddMinutes(5))], AttendanceTimeline.Build([leave, join], Alice));
    }

    [Fact]
    public void Other_event_types_are_not_presence()
    {
        Assert.Empty(AttendanceTimeline.Build([Ev("room_started", null, 0), Ev("track_published", Alice, 1)], Alice));
        Assert.False(AttendanceTimeline.IsPresenceEvent("track_published"));
        Assert.True(AttendanceTimeline.IsPresenceEvent("participant_left"));
    }
}
