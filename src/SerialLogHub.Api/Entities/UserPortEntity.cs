namespace SerialLogHub.Api.Entities;

public class UserPortEntity
{
    public int Id { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string Ports { get; set; } = "[]";
}
