using System.Collections.Concurrent;

namespace SerialLogHub.Web.Services;

/// <summary>
/// Tracks multiple SignalR connections per authenticated operator so multi-tab usage works seamlessly.
/// </summary>
public class UserConnectionManager
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _userConnections = new(StringComparer.OrdinalIgnoreCase);

    public void AddConnection(string userEmail, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(userEmail) || string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        string key = userEmail.Trim().ToLowerInvariant();
        var set = _userConnections.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>());
        set[connectionId] = 0;
    }

    public void RemoveConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return;
        }

        foreach (var kvp in _userConnections)
        {
            if (kvp.Value.TryRemove(connectionId, out _) && kvp.Value.IsEmpty)
            {
                _userConnections.TryRemove(kvp.Key, out _);
            }
        }
    }

    public IReadOnlyList<string> GetConnectionIds(string userEmail)
    {
        if (string.IsNullOrWhiteSpace(userEmail))
        {
            return Array.Empty<string>();
        }

        string key = userEmail.Trim().ToLowerInvariant();
        if (_userConnections.TryGetValue(key, out var set) && !set.IsEmpty)
        {
            return set.Keys.ToList();
        }

        return Array.Empty<string>();
    }
}
