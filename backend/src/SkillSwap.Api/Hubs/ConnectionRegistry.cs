using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using SkillSwap.Application.Moderation;
using SkillSwap.Application.Profile;
using SkillSwap.Domain.Enums;

namespace SkillSwap.Api.Hubs;

/// <summary>
/// Открытые соединения SignalR по пользователям (оба хаба). Нужен, чтобы после блокировки аккаунта сразу оборвать его соединения: токен доступа живёт ещё какое-то
/// время, и без этого заблокированный продолжал бы получать события. Хранится в памяти процесса (при нескольких экземплярах каждый отключает свои соединения).
/// </summary>
public sealed class ConnectionRegistry : IRealtimeDisconnector
{
    private readonly ConcurrentDictionary<string, (Guid UserId, HubCallerContext Context)> _connections = new();

    public void Add(Guid userId, HubCallerContext context) => _connections[context.ConnectionId] = (userId, context);

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public int CountFor(Guid userId) => _connections.Values.Count(c => c.UserId == userId);

    public Task DisconnectUserAsync(Guid userId, CancellationToken ct)
    {
        foreach (var (connectionId, entry) in _connections.ToArray().Where(c => c.Value.UserId == userId))
        {
            entry.Context.Abort();
            _connections.TryRemove(connectionId, out _);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Принять соединение: пользователь должен быть действующим (заблокированный с ещё живым токеном подключиться не может). Возвращает false, если соединение оборвано.
    /// </summary>
    public async Task<bool> AcceptAsync(HubCallerContext context, IProfileStore profiles)
    {
        if (!Guid.TryParse(context.User?.FindFirst("sub")?.Value, out var userId)
            || await profiles.FindUserAsync(userId, context.ConnectionAborted) is not { Status: UserStatus.Active })
        {
            context.Abort();
            return false;
        }

        Add(userId, context);
        return true;
    }
}
