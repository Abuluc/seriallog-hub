namespace SerialLogHub.Api.Entities;

public class TelemetryRecordEntity
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public double Humidity { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public DateTime IngestedAt { get; set; } = DateTime.UtcNow;
    public bool IsAnomaly { get; set; }
    public string AnomalySeverity { get; set; } = "Normal";
    public string AnomalyMethod { get; set; } = "None";
    public string AnomalyExplanation { get; set; } = string.Empty;
    public string? RawPayload { get; set; }

    public DeviceEntity? Device { get; set; }
}
