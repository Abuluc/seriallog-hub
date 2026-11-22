using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SerialLogHub.Web.Services;

namespace SerialLogHub.Web.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    private readonly UserConnectionManager _connectionManager;

    public NotificationHub(UserConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
    }

    public override async Task OnConnectedAsync()
    {
        string? authenticatedEmail = Context.User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(authenticatedEmail))
        {
            _connectionManager.AddConnection(authenticatedEmail, Context.ConnectionId);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _connectionManager.RemoveConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public Task UserDisconnected()
    {
        _connectionManager.RemoveConnection(Context.ConnectionId);
        return Task.CompletedTask;
    }
}
