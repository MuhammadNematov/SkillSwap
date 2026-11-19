using SkillSwap.Domain.Entities;

namespace SkillSwap.Domain.Tests.Entities;

public class LessonSessionRoomClosedTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_closing_moment_is_set_once_and_repeats_do_not_move_it()
    {
        var s = LessonSession.Propose(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now.AddHours(5), null, Now, Now.AddDays(30));
        Assert.Null(s.RoomClosedAt);

        s.MarkRoomClosed(Now.AddHours(7));
        s.MarkRoomClosed(Now.AddHours(9));

        Assert.Equal(Now.AddHours(7), s.RoomClosedAt);
    }
}
