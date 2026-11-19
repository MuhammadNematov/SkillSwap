using SkillSwap.Application.Abstractions;

namespace SkillSwap.Api.IntegrationTests.Infrastructure;

/// <summary>Заглушка серверного API LiveKit: запоминает, какие комнаты «удалили», и может изобразить сбой медиасервера.</summary>
public sealed class FakeLiveKitRooms : ILiveKitRoomService
{
    private readonly object _gate = new();
    private readonly List<string> _deleted = [];

    public bool IsConfigured { get; set; } = true;

    /// <summary>Пока true, удаление комнаты отвечает «LiveKit недоступен».</summary>
    public bool Fail { get; set; }

    public IReadOnlyList<string> Deleted
    {
        get
        {
            lock (_gate)
            {
                return _deleted.ToList();
            }
        }
    }

    public Task DeleteRoomAsync(string room, CancellationToken ct)
    {
        if (Fail)
        {
            throw new LiveKitUnavailableException("LiveKit недоступен (заглушка).");
        }

        lock (_gate)
        {
            _deleted.Add(room);
        }

        return Task.CompletedTask;
    }
}
