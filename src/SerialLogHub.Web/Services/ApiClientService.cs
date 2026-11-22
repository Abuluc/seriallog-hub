using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SerialLogHub.Web.Models;

namespace SerialLogHub.Web.Services;

public class ApiClientService
{
    private const string DefaultRouteCulture = "en-US";
    private const string OperatorEmailHeaderName = "X-OPERATOR-EMAIL";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ApiClientService> _logger;

    public ApiClientService(HttpClient httpClient, IConfiguration configuration, ILogger<ApiClientService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    private static HttpRequestMessage CreateScopedRequest(HttpMethod method, string relativeUri, string operatorEmail, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, relativeUri)
        {
            Content = content
        };
        if (!string.IsNullOrWhiteSpace(operatorEmail))
        {
            req.Headers.Add(OperatorEmailHeaderName, operatorEmail.Trim());
        }
        return req;
    }

    public async Task<(bool Success, string Content)> LoginAsync(UserModel model, CancellationToken ct = default)
    {
        var payload = new UserModel
        {
            Username = model.Username,
            Email = model.Email,
            HashedPassword = HashHelper.ComputeSha256(model.HashedPassword),
            VerificationToken = "none",
            ResetPasswordToken = "none"
        };

        using var response = await _httpClient.PostAsJsonAsync("Auth/login", payload, ct);
        string content = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, content);
    }

    public async Task<(bool Success, string Content)> RegisterAsync(UserModel model, CancellationToken ct = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"auth/{DefaultRouteCulture}/register", model, ct);
        string content = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, content);
    }

    public async Task<(bool Success, string Content)> UpdateNameAsync(UpdateNameRequestModel request, CancellationToken ct = default)
    {
        var payload = new
        {
            email = request.Email,
            newValue = request.NewValue,
            currentPassword = HashHelper.ComputeSha256(request.CurrentPassword)
        };

        using var req = CreateScopedRequest(HttpMethod.Post, "auth/updatename", request.Email, JsonContent.Create(payload));
        using var response = await _httpClient.SendAsync(req, ct);
        string content = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, content);
    }

    public async Task<UserProfileDto?> GetProfileAsync(string email, CancellationToken ct = default)
    {
        using var req = CreateScopedRequest(HttpMethod.Get, $"Auth/profile?email={Uri.EscapeDataString(email)}", email);
        using var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<UserProfileDto>(JsonOptions, ct);
    }

    public async Task<(bool Success, string Content)> SendResetMailAsync(string email, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(email);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"Auth/{DefaultRouteCulture}/SendResetMail", content, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, body);
    }

    public async Task<(bool Success, string Content)> ResendVerificationEmailAsync(string email, CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(email);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync($"Auth/{DefaultRouteCulture}/resendverificationemail", content, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        return (response.IsSuccessStatusCode, body);
    }

    public async Task<List<string>> GetPortsAsync(string email, CancellationToken ct = default)
    {
        using var req = CreateScopedRequest(HttpMethod.Get, $"user/getports?email={Uri.EscapeDataString(email)}", email);
        using var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            return new List<string>();
        }

        var result = await response.Content.ReadFromJsonAsync<PortsApiResponse>(JsonOptions, ct);
        return result?.Ports ?? new List<string>();
    }

    public async Task<string> GetCommandHistoryJsonAsync(string email, int take = 25, CancellationToken ct = default)
    {
        using var req = CreateScopedRequest(HttpMethod.Get, $"user/history?email={Uri.EscapeDataString(email)}&take={Math.Clamp(take, 1, 100)}", email);
        using var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            return "[]";
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> GetDevicesJsonAsync(string email, CancellationToken ct = default)
    {
        using var req = CreateScopedRequest(HttpMethod.Get, $"telemetry/devices?email={Uri.EscapeDataString(email)}", email);
        using var response = await _httpClient.SendAsync(req, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : "[]";
    }

    public async Task<string> GetTelemetryHistoryJsonAsync(
        string email,
        int? deviceId,
        DateTime? fromUtc,
        DateTime? toUtc,
        bool anomaliesOnly,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder($"telemetry/history?email={Uri.EscapeDataString(email)}&anomaliesOnly={anomaliesOnly}&pageSize=500");
        if (deviceId.HasValue) sb.Append($"&deviceId={deviceId.Value}");
        if (fromUtc.HasValue) sb.Append($"&fromUtc={Uri.EscapeDataString(fromUtc.Value.ToString("O"))}");
        if (toUtc.HasValue) sb.Append($"&toUtc={Uri.EscapeDataString(toUtc.Value.ToString("O"))}");

        using var req = CreateScopedRequest(HttpMethod.Get, sb.ToString(), email);
        using var response = await _httpClient.SendAsync(req, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : "{}";
    }

    public async Task<string> CompareDevicesJsonAsync(string email, int deviceA, int deviceB, CancellationToken ct = default)
    {
        string url = $"telemetry/compare?email={Uri.EscapeDataString(email)}&deviceA={deviceA}&deviceB={deviceB}";
        using var req = CreateScopedRequest(HttpMethod.Get, url, email);
        using var response = await _httpClient.SendAsync(req, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : "{}";
    }

    public async Task<string> IngestTelemetryJsonAsync(string email, string portName, string? deviceName, string rawCsvPayload, CancellationToken ct = default)
    {
        var payload = new
        {
            Email = email,
            PortName = portName,
            DeviceName = deviceName,
            RawCsvPayload = rawCsvPayload
        };

        using var req = CreateScopedRequest(HttpMethod.Post, "telemetry/ingest", email, JsonContent.Create(payload));
        using var response = await _httpClient.SendAsync(req, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : "{}";
    }

    public async Task<bool> SendCommandAsync(CommandModel model, CancellationToken ct = default)
    {
        using var req = CreateScopedRequest(HttpMethod.Post, "user/SendCommand", model.Email, JsonContent.Create(model));
        using var response = await _httpClient.SendAsync(req, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<CommandModel?> GetCommandResponseAsync(string userEmail, string commandId, CancellationToken ct = default)
    {
        string url = $"user/getresponses?email={Uri.EscapeDataString(userEmail)}&CommandID={Uri.EscapeDataString(commandId)}";
        using var req = CreateScopedRequest(HttpMethod.Get, url, userEmail);
        using var response = await _httpClient.SendAsync(req, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<CommandModel>(JsonOptions, ct);
    }

    public async Task<CommandModel?> SendAndWaitForResponseAsync(CommandModel command, CancellationToken ct = default)
    {
        bool sent = await SendCommandAsync(command, ct);
        if (!sent || string.IsNullOrWhiteSpace(command.CommandID))
        {
            return null;
        }

        int timeoutSeconds = _configuration.GetValue("ApiSettings:CommandTimeoutSeconds", 30);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 3, 120)));

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                var current = await GetCommandResponseAsync(command.Email, command.CommandID, timeoutCts.Token);
                if (current != null &&
                    (current.IsProcessed ||
                     (!string.IsNullOrWhiteSpace(current.Response) &&
                      !string.Equals(current.Response, "Pending", StringComparison.OrdinalIgnoreCase) &&
                      !string.Equals(current.Response, "Response yok", StringComparison.OrdinalIgnoreCase))))
                {
                    return current;
                }

                await Task.Delay(500, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out waiting for command {CommandId} response after {Timeout}s.", command.CommandID, timeoutSeconds);
        }

        return null;
    }
}
