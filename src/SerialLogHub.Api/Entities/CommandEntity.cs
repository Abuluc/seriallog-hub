namespace SerialLogHub.Api.Entities;

public class CommandEntity
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string SelecetdPort { get; set; } = string.Empty;
    public int BaudRate { get; set; } = 9600;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string CommandID { get; set; } = string.Empty;
    public bool IsProcessed { get; set; }
    public string Response { get; set; } = "Pending";
}
