using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SerialLogHub.Desktop.Models;

namespace SerialLogHub.Desktop.Services;

public sealed class BridgeApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _apiHttpClient;
    private readonly HttpClient _webHttpClient;

    public BridgeApiClient(BridgeConfiguration config)
    {
        string apiBase = config.ApiBaseUrl.EndsWith('/') ? config.ApiBaseUrl : config.ApiBaseUrl + "/";
        _apiHttpClient = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(15) };
        _apiHttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            _apiHttpClient.DefaultRequestHeaders.Add("X-API-KEY", config.ApiKey);
        }

        string webBase = config.WebBridgeBaseUrl.EndsWith('/') ? config.WebBridgeBaseUrl : config.WebBridgeBaseUrl + "/";
        _webHttpClient = new HttpClient { BaseAddress = new Uri(webBase), Timeout = TimeSpan.FromSeconds(10) };
        _webHttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(config.BridgeKey))
        {
            _webHttpClient.DefaultRequestHeaders.Add("X-BRIDGE-KEY", config.BridgeKey);
        }
    }

    public static string ComputeSha256(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public async Task<(bool Success, string Message)> LoginAsync(string email, string username, string password, CancellationToken ct = default)
    {
        var payload = new UserCredentialsRequest
        {
            Email = email.Trim(),
            Username = username.Trim(),
            HashedPassword = ComputeSha256(password)
        };

        using var response = await _apiHttpClient.PostAsJsonAsync("Auth/login", payload, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, body);
    }

    public async Task<bool> UpdatePortsAsync(string email, IReadOnlyList<string> ports, CancellationToken ct = default)
    {
        var payload = new UserPortUpdateRequest
        {
            UserEmail = email.Trim(),
            Ports = ports.ToList()
        };

        using var response = await _apiHttpClient.PostAsJsonAsync("user/updateports", payload, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<DeviceCommandDto?> GetPendingCommandAsync(string email, CancellationToken ct = default)
    {
        using var response = await _apiHttpClient.GetAsync($"user/getcommands?email={Uri.EscapeDataString(email.Trim())}", ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DeviceCommandDto>(JsonOptions, ct);
    }

    public async Task<bool> UpdateCommandResponseAsync(string email, string? commandId, string responseText, CancellationToken ct = default)
    {
        var payload = new CommandUpdateRequest
        {
            Email = email.Trim(),
            CommandId = commandId,
            Response = responseText
        };

        using var response = await _apiHttpClient.PostAsJsonAsync("user/UpdateCommand", payload, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SendWebHeartbeatAsync(string email, CancellationToken ct = default)
    {
        var payload = new
        {
            AppName = "ConsoleApp",
            Message = "BridgeOnline",
            UserName = email.Trim()
        };

        using var response = await _webHttpClient.PostAsJsonAsync("Home/AppCheck", payload, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task SendStreamProgressAsync(string email, int rowCount, bool finished, string fullData, CancellationToken ct = default)
    {
        var payload = new
        {
            AppName = "ConsoleApp2",
            Message = finished ? "Finish" : $"{rowCount} rows read...",
            Data = finished ? fullData : string.Empty,
            UserName = email.Trim()
        };

        try
        {
            using var _ = await _webHttpClient.PostAsJsonAsync("Home/waitresponse", payload, ct);
        }
        catch
        {
            // Non-fatal if web tab is not currently connected via SignalR
        }
    }

    public void Dispose()
    {
        _apiHttpClient.Dispose();
        _webHttpClient.Dispose();
    }
}
