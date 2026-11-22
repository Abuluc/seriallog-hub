namespace SerialLogHub.Api.Entities;

public static class CommandLifecycleStatus
{
    public const string Pending = "Pending";
    public const string Dispatched = "Dispatched";
    public const string Executing = "Executing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string TimedOut = "TimedOut";
    public const string Cancelled = "Cancelled";
}

public class CommandEntity
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Preserves the legacy "SelecetdPort" column/property name required by the Desktop Bridge contract.
    /// </summary>
    public string SelecetdPort { get; set; } = string.Empty;

    public int BaudRate { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Response { get; set; } = "Pending";
    public string CommandID { get; set; } = string.Empty;
    public bool IsProcessed { get; set; }

    /// <summary>
    /// Explicit command lifecycle state: Pending, Dispatched, Executing, Completed, Failed, TimedOut, Cancelled.
    /// </summary>
    public string Status { get; set; } = CommandLifecycleStatus.Pending;

    public DateTime? DispatchedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorCategory { get; set; }
    public int AttemptCount { get; set; }
}
