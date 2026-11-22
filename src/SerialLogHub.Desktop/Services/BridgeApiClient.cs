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
    private readonly string _apiKey;
    private string? _operatorToken;

    public BridgeApiClient(BridgeConfiguration config)
    {
        _apiKey = config.ApiKey ?? string.Empty;

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

    private void AttachOperatorCredentials(HttpRequestMessage req, string email)
    {
        string cleanEmail = email.Trim().ToLowerInvariant();
        req.Headers.Add("X-OPERATOR-EMAIL", cleanEmail);

        if (!string.IsNullOrWhiteSpace(_operatorToken))
        {
            req.Headers.Add("X-OPERATOR-TOKEN", _operatorToken);
        }
        else if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            long unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            byte[] hmac = HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(_apiKey),
                Encoding.UTF8.GetBytes($"{cleanEmail}|{unixSeconds}"));
            req.Headers.Add("X-OPERATOR-TIMESTAMP", unixSeconds.ToString());
            req.Headers.Add("X-OPERATOR-PROOF", Convert.ToHexString(hmac).ToLowerInvariant());
        }
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

        if (response.IsSuccessStatusCode)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("operatorToken", out var tokenProp) &&
                    !string.IsNullOrWhiteSpace(tokenProp.GetString()))
                {
                    _operatorToken = tokenProp.GetString();
                }
            }
            catch
            {
                // Keep HMAC fallback if response JSON has no operatorToken
            }
        }

        return (response.IsSuccessStatusCode, body);
    }

    public async Task<bool> UpdatePortsAsync(string email, IReadOnlyList<string> ports, CancellationToken ct = default)
    {
        string cleanEmail = email.Trim();
        var payload = new UserPortUpdateRequest
        {
            UserEmail = cleanEmail,
            Ports = ports.ToList()
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "user/updateports")
        {
            Content = JsonContent.Create(payload)
        };
        AttachOperatorCredentials(req, cleanEmail);

        using var response = await _apiHttpClient.SendAsync(req, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<DeviceCommandDto?> GetPendingCommandAsync(string email, CancellationToken ct = default)
    {
        string cleanEmail = email.Trim();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"user/getcommands?email={Uri.EscapeDataString(cleanEmail)}");
        AttachOperatorCredentials(req, cleanEmail);

        using var response = await _apiHttpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DeviceCommandDto>(JsonOptions, ct);
    }

    public async Task<bool> UpdateCommandResponseAsync(string email, string? commandId, string responseText, CancellationToken ct = default)
    {
        string cleanEmail = email.Trim();
        var payload = new CommandUpdateRequest
        {
            Email = cleanEmail,
            CommandId = commandId,
            Response = responseText
        };

        int[] backoffMs = { 0, 250, 750 };
        for (int attempt = 0; attempt < backoffMs.Length; attempt++)
        {
            if (backoffMs[attempt] > 0)
            {
                await Task.Delay(backoffMs[attempt], ct);
            }

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "user/UpdateCommand")
                {
                    Content = JsonContent.Create(payload)
                };
                AttachOperatorCredentials(req, cleanEmail);

                using var response = await _apiHttpClient.SendAsync(req, ct);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Retry on transient transport error
            }
        }

        return false;
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
