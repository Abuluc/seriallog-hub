namespace SerialLogHub.Api.Entities;

public class DeviceEntity
{
    public int Id { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    public ICollection<TelemetryRecordEntity> TelemetryRecords { get; set; } = new List<TelemetryRecordEntity>();
}
