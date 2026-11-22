namespace SerialLogHub.Api.Entities;

public class ApiKeyEntity
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddYears(1);
}
