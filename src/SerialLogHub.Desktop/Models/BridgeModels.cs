using System.Text.Json.Serialization;

namespace SerialLogHub.Desktop.Models;

public sealed class BridgeConfiguration
{
    public string ApiBaseUrl { get; set; } = "http://localhost:8080/api/";
    public string WebBridgeBaseUrl { get; set; } = "http://localhost:3000/";
    public string ApiKey { get; set; } = string.Empty;
    public string BridgeKey { get; set; } = string.Empty;
    public int PortScanIntervalMs { get; set; } = 3000;
    public int CommandPollIntervalMs { get; set; } = 1500;
    public int HeartbeatIntervalMs { get; set; } = 3000;
    public bool EnableVirtualDemoDevice { get; set; } = true;
    public string VirtualDemoPortName { get; set; } = "COM99 (Virtual DataLogger - Demo)";
    public string DemoEmail { get; set; } = "demo@serialloghub.local";
    public string DemoUsername { get; set; } = "demo_operator";
}

public sealed class UserCredentialsRequest
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string HashedPassword { get; set; } = string.Empty;
    public bool IsEmailVerified { get; set; }
    public string VerificationToken { get; set; } = "None";
    public string ResetPasswordToken { get; set; } = "None";
}

public sealed class UserPortUpdateRequest
{
    public string UserEmail { get; set; } = string.Empty;

    [JsonPropertyName("ports")]
    public List<string> Ports { get; set; } = new();
}

public sealed class DeviceCommandDto
{
    [JsonPropertyName("CommandID")]
    public string CommandId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 9600;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsProcessed { get; set; }
    public string Response { get; set; } = string.Empty;
}

public sealed class CommandUpdateRequest
{
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("CommandID")]
    public string? CommandId { get; set; }

    public string Response { get; set; } = string.Empty;
}
