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

    public static string GetUserGroupName(string email)
        => $"user:{email.Trim().ToLowerInvariant()}";

    public static string GetDeviceGroupName(string email, string portName)
        => $"device:{email.Trim().ToLowerInvariant()}:{portName.Trim().ToUpperInvariant()}";

    public override async Task OnConnectedAsync()
    {
        string? authenticatedEmail = Context.User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(authenticatedEmail))
        {
            _connectionManager.AddConnection(authenticatedEmail, Context.ConnectionId);
            await Groups.AddToGroupAsync(Context.ConnectionId, GetUserGroupName(authenticatedEmail));
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        string? authenticatedEmail = Context.User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(authenticatedEmail))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetUserGroupName(authenticatedEmail));
        }

        _connectionManager.RemoveConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinDeviceGroup(string portName)
    {
        string? authenticatedEmail = Context.User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(authenticatedEmail) && !string.IsNullOrWhiteSpace(portName))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GetDeviceGroupName(authenticatedEmail, portName));
        }
    }

    public async Task LeaveDeviceGroup(string portName)
    {
        string? authenticatedEmail = Context.User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(authenticatedEmail) && !string.IsNullOrWhiteSpace(portName))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GetDeviceGroupName(authenticatedEmail, portName));
        }
    }

    public Task UserDisconnected()
    {
        _connectionManager.RemoveConnection(Context.ConnectionId);
        return Task.CompletedTask;
    }
}
