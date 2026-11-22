using System.Text.Json.Serialization;

namespace SerialLogHub.Api.Models;

public class SendCommandRequest
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;

    // Preserves legacy typo from WhiloggerWEB CommandModel while also accepting "Port"
    [JsonPropertyName("SelecetdPort")]
    public string? SelecetdPort { get; set; }

    [JsonPropertyName("Port")]
    public string? Port { get; set; }

    public int BaudRate { get; set; } = 9600;
    public string? Message { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("CommandID")]
    public string? CommandID { get; set; }

    public string? Response { get; set; }

    public string ResolvedPort => !string.IsNullOrWhiteSpace(SelecetdPort) ? SelecetdPort : (Port ?? string.Empty);
}

public class UpdateCommandRequest
{
    public string Email { get; set; } = string.Empty;
    public string? CommandID { get; set; }
    public string Response { get; set; } = string.Empty;
}

public class CommandResponseDto
{
    public int Id { get; set; }

    [JsonPropertyName("CommandID")]
    public string CommandID { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("SelecetdPort")]
    public string SelecetdPort { get; set; } = string.Empty;

    [JsonPropertyName("Port")]
    public string Port => SelecetdPort;

    public int BaudRate { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsProcessed { get; set; }
    public string Response { get; set; } = string.Empty;
}
