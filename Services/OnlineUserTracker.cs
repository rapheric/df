using System.Collections.Concurrent;
using NCBA.DCL.Models;

namespace NCBA.DCL.Services;

public class OnlineUserTracker
{
    private readonly ConcurrentDictionary<Guid, OnlineUser> _users = new();

    public IReadOnlyCollection<OnlineUser> GetAll() => _users.Values;

    public void AddSocket(Guid userId, string socketId, OnlineUser info)
    {
        _users.AddOrUpdate(userId, id =>
        {
            info.SocketIds.Add(socketId);
            return info;
        }, (id, existing) =>
        {
            existing.SocketIds.Add(socketId);
            existing.LastSeen = DateTime.UtcNow;
            existing.CurrentPage = info.CurrentPage ?? existing.CurrentPage;
            return existing;
        });
    }

    public void RemoveSocket(Guid userId, string socketId)
    {
        if (_users.TryGetValue(userId, out var user))
        {
            user.SocketIds.Remove(socketId);
            if (user.SocketIds.Count == 0)
            {
                _users.TryRemove(userId, out _);
            }
        }
    }

    public OnlineUser? Get(Guid userId)
    {
        _users.TryGetValue(userId, out var u);
        return u;
    }

    public bool ForceLogout(Guid userId, out OnlineUser? removed)
    {
        return _users.TryRemove(userId, out removed);
    }
}
