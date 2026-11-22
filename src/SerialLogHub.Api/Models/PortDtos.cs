using System.Text.Json.Serialization;

namespace SerialLogHub.Api.Models;

public class UpdatePortsRequest
{
    public int Id { get; set; }
    public string UserEmail { get; set; } = string.Empty;

    [JsonPropertyName("ports")]
    public List<string> Ports { get; set; } = new();
}

public class GetPortsResponse
{
    public List<string> Ports { get; set; } = new();
}
