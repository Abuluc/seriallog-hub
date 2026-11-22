namespace SerialLogHub.Api.Entities;

public class CommandHistoryEntity
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 9600;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string CommandID { get; set; } = string.Empty;
    public bool IsProcessed { get; set; }
    public string Response { get; set; } = string.Empty;
}
