using System.Collections.Concurrent;

namespace SerialLogHub.Web.Services;

public class UserConnectionManager
{
    private readonly ConcurrentDictionary<string, string> _userConnections = new(StringComparer.OrdinalIgnoreCase);

    public void AddConnection(string userEmail, string connectionId)
    {
        if (!string.IsNullOrWhiteSpace(userEmail) && !string.IsNullOrWhiteSpace(connectionId))
        {
            _userConnections[userEmail.Trim().ToLowerInvariant()] = connectionId;
        }
    }

    public void RemoveConnection(string connectionId)
    {
        var match = _userConnections.FirstOrDefault(x => x.Value == connectionId);
        if (!string.IsNullOrEmpty(match.Key))
        {
            _userConnections.TryRemove(match.Key, out _);
        }
    }

    public string? GetConnectionId(string userEmail)
    {
        if (string.IsNullOrWhiteSpace(userEmail))
        {
            return null;
        }

        _userConnections.TryGetValue(userEmail.Trim().ToLowerInvariant(), out var connectionId);
        return connectionId;
    }
}
