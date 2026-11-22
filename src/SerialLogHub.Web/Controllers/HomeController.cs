using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SerialLogHub.Web.Hubs;
using SerialLogHub.Web.Models;
using SerialLogHub.Web.Services;

namespace SerialLogHub.Web.Controllers;

public sealed class ServerIngestPayload
{
    public string PortName { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string RawCsvPayload { get; set; } = string.Empty;
}

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ApiClientService _apiClient;
    private readonly IHubContext<NotificationHub> _hubContext;
    private readonly UserConnectionManager _connectionManager;
    private readonly IConfiguration _configuration;

    public HomeController(
        ILogger<HomeController> logger,
        ApiClientService apiClient,
        IHubContext<NotificationHub> hubContext,
        UserConnectionManager connectionManager,
        IConfiguration configuration)
    {
        _logger = logger;
        _apiClient = apiClient;
        _hubContext = hubContext;
        _connectionManager = connectionManager;
        _configuration = configuration;
    }

    private string? CurrentUserEmail => User.Identity?.IsAuthenticated == true ? User.Identity.Name : null;

    public IActionResult KVKK() => View();

    [Authorize]
    [HttpGet("Home/DataTable")]
    [HttpGet("Home/CrateTable")]
    public IActionResult DataTable() => View("CrateTable");

    [Authorize]
    public IActionResult Graph() => View();

    [Authorize]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var availablePorts = await GetUserPortsAsync(ct);
        var model = new SerialPortModel
        {
            AvailablePorts = availablePorts,
            BaudRates = new List<int> { 9600, 19200, 38400, 57600, 115200 },
            DemoModeEnabled = _configuration.GetValue("DemoMode:Enabled", false)
        };

        return View(model);
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> AppCheck([FromBody] AppHealthPayload payload, CancellationToken ct)
    {
        if (!IsBridgeAuthorized())
        {
            return Unauthorized("Invalid bridge key.");
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.UserName))
        {
            return BadRequest("Invalid payload.");
        }

        if (payload.AppName == "ConsoleApp")
        {
            var connectionIds = _connectionManager.GetConnectionIds(payload.UserName);
            if (connectionIds.Count > 0)
            {
                await _hubContext.Clients.Clients(connectionIds)
                    .SendAsync("ReceiveMessage", payload.AppName, payload.Message, ct);
                return Ok("Data sent to the user.");
            }

            return NotFound("User not connected.");
        }

        return BadRequest("Invalid app name.");
    }

    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> waitresponse([FromBody] WaitResponsePayload payload, CancellationToken ct)
    {
        if (!IsBridgeAuthorized())
        {
            return Unauthorized("Invalid bridge key.");
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.UserName))
        {
            return BadRequest("Invalid payload.");
        }

        if (payload.AppName == "ConsoleApp2")
        {
            var connectionIds = _connectionManager.GetConnectionIds(payload.UserName);
            if (connectionIds.Count > 0)
            {
                await _hubContext.Clients.Clients(connectionIds)
                    .SendAsync("DataReading", payload.AppName, payload.Message, payload.Data, ct);
                return Ok("Data sent to the user.");
            }

            return NotFound("User not connected.");
        }

        return BadRequest("Invalid app name.");
    }

    [Authorize]
    [HttpPost]
    public async Task<JsonResult> Send([FromBody] SerialPortModel model, CancellationToken ct)
    {
        string? userEmail = CurrentUserEmail;
        if (string.IsNullOrEmpty(userEmail))
        {
            return Json(new { status = "error", message = "User session not found." });
        }

        if (model == null || IsPlaceholderPort(model.SelectedPort) || string.IsNullOrWhiteSpace(model.MessageToSend))
        {
            return Json(new { status = "error", message = "Please select a valid port and enter a command." });
        }

        var command = new CommandModel
        {
            Email = userEmail,
            SelecetdPort = model.SelectedPort,
            BaudRate = model.SelectedBaudRate > 0 ? model.SelectedBaudRate : 9600,
            Message = model.MessageToSend.Trim(),
            Timestamp = DateTime.UtcNow,
            CommandID = Guid.NewGuid().ToString(),
            Response = "Pending"
        };

        var result = await _apiClient.SendAndWaitForResponseAsync(command, ct);
        if (result == null)
        {
            return Json(new
            {
                status = "timeout",
                message = "No response received from Desktop COM Bridge within timeout.",
                ResponseMessage = string.Empty
            });
        }

        if (!string.IsNullOrWhiteSpace(result.Response) &&
            result.Response.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new
            {
                status = "error",
                message = result.Response,
                ResponseMessage = result.Response
            });
        }

        return Json(new
        {
            status = "success",
            message = "Command executed.",
            ResponseMessage = result.Response
        });
    }

    [Authorize]
    [HttpPost]
    public async Task<JsonResult> TestConnection(SerialPortModel model, CancellationToken ct)
    {
        string? userEmail = CurrentUserEmail;
        if (string.IsNullOrEmpty(userEmail))
        {
            return Json(new { status = "Error", message = "User session not found." });
        }

        if (model == null || IsPlaceholderPort(model.SelectedPort))
        {
            return Json(new { status = "Error", message = "Please select a valid COM port." });
        }

        var command = new CommandModel
        {
            Email = userEmail,
            SelecetdPort = model.SelectedPort,
            BaudRate = model.SelectedBaudRate > 0 ? model.SelectedBaudRate : 9600,
            Message = "TEST",
            Timestamp = DateTime.UtcNow,
            CommandID = Guid.NewGuid().ToString(),
            Response = "Pending"
        };

        var response = await _apiClient.SendAndWaitForResponseAsync(command, ct);
        if (response == null || string.IsNullOrWhiteSpace(response.Response))
        {
            Response.Cookies.Delete("Connection");
            return Json(new
            {
                status = "Error",
                message = $"No response from {model.SelectedPort}. Ensure the Desktop COM Bridge is running and a DataLogger is attached.",
                ResponseMessage = response
            });
        }

        string rawResponse = response.Response.Trim();
        if (rawResponse.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            Response.Cookies.Delete("Connection");
            return Json(new
            {
                status = "Error",
                message = rawResponse,
                ResponseMessage = response
            });
        }

        string lower = rawResponse.ToLowerInvariant();
        bool validHandshake = lower.Contains("cmd,ok,lcz,") || lower.Contains("cmd,ok,lss,");
        if (!validHandshake)
        {
            Response.Cookies.Delete("Connection");
            return Json(new
            {
                status = "Error",
                message = $"Invalid DataLogger handshake on {model.SelectedPort}. Port responded with unexpected data instead of a DataLogger signature.",
                ResponseMessage = response
            });
        }

        var connectionData = new
        {
            Connected = true,
            currentPort = model.SelectedPort,
            currentBaud = command.BaudRate
        };

        Response.Cookies.Append("Connection", JsonSerializer.Serialize(connectionData), new CookieOptions
        {
            HttpOnly = false,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(1),
            Path = "/"
        });

        return Json(new { status = "success", message = "Connected.", ResponseMessage = response });
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> CommandHistory(int take = 25, CancellationToken ct = default)
    {
        string? userEmail = CurrentUserEmail;
        if (string.IsNullOrEmpty(userEmail))
        {
            return Unauthorized();
        }

        string json = await _apiClient.GetCommandHistoryJsonAsync(userEmail, take, ct);
        return Content(json, "application/json", Encoding.UTF8);
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> ServerDevices(CancellationToken ct = default)
    {
        string? userEmail = CurrentUserEmail;
        if (string.IsNullOrEmpty(userEmail)) return Unauthorized();
        string json = await _apiClient.GetDevicesJsonAsync(userEmail, ct);
        return Content(json, "application/json", Encoding.UTF8);
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> ServerTelemetryHistory(
        int? deviceId = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        bool anomaliesOnly = false,
        CancellationToken ct = default)
    {
        string? userEmail = CurrentUserEmail;
        if (string.IsNullOrEmpty(userEmail)) return Unauthorized();
        string json = await _apiClient.GetTelemetryHistoryJsonAsync(userEmail, deviceId, fromUtc, toUtc, anomaliesOnly, ct);
        return Content(json, "application/json", Encoding.UTF8);
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> CompareDevices(int deviceA, int deviceB, CancellationToken ct = default)
    {
        string? userEmail = CurrentUserEmail;
        if (string.IsNullOrEmpty(userEmail)) return Unauthorized();
        string json = await _apiClient.CompareDevicesJsonAsync(userEmail, deviceA, deviceB, ct);
        return Content(json, "application/json", Encoding.UTF8);
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> IngestTelemetry([FromBody] ServerIngestPayload payload, CancellationToken ct = default)
    {
        string? userEmail = CurrentUserEmail;
        if (string.IsNullOrEmpty(userEmail)) return Unauthorized();
        if (payload == null || string.IsNullOrWhiteSpace(payload.PortName) || string.IsNullOrWhiteSpace(payload.RawCsvPayload))
        {
            return BadRequest(new { message = "PortName and RawCsvPayload are required." });
        }

        string json = await _apiClient.IngestTelemetryJsonAsync(userEmail, payload.PortName, payload.DeviceName, payload.RawCsvPayload, ct);
        return Content(json, "application/json", Encoding.UTF8);
    }

    private static bool IsPlaceholderPort(string? port)
    {
        if (string.IsNullOrWhiteSpace(port)) return true;
        return port.Equals("No COM ports detected", StringComparison.OrdinalIgnoreCase) ||
               port.Equals("No active session", StringComparison.OrdinalIgnoreCase);
    }

    [Authorize]
    [HttpGet("Home/GetAvailablePorts")]
    [HttpGet("Home/Getavabileport")]
    public async Task<IActionResult> GetAvailablePorts(CancellationToken ct)
    {
        var ports = await GetUserPortsAsync(ct);
        return Ok(ports);
    }

    [Authorize]
    [HttpPost]
    public async Task<JsonResult> SetDelayStart(CalibrationModel model, string Time, CancellationToken ct)
    {
        string? userEmail = CurrentUserEmail;
        if (string.IsNullOrEmpty(userEmail) || string.IsNullOrWhiteSpace(model.SelectedPort))
        {
            return Json(new { status = "error", message = "Valid session and port are required." });
        }

        var command = new CommandModel
        {
            Email = userEmail,
            SelecetdPort = model.SelectedPort,
            BaudRate = model.SelectedBaudRate > 0 ? model.SelectedBaudRate : 9600,
            Message = $"WLS0,{Time}",
            Timestamp = DateTime.UtcNow,
            CommandID = Guid.NewGuid().ToString(),
            Response = "Pending"
        };

        var response = await _apiClient.SendAndWaitForResponseAsync(command, ct);
        return response != null
            ? Json(new { status = "success", message = "Delayed start configured.", ResponseMessage = response })
            : Json(new { status = "timeout", message = "No response from device.", ResponseMessage = "" });
    }

    [Authorize]
    [HttpPost]
    public JsonResult UpdatePorts([FromBody] SerialPortModel model)
    {
        if (model.AvailablePorts.Count > 0)
        {
            return Json(new { success = true, message = "Ports updated." });
        }

        return Json(new { success = false, message = "Port list is empty." });
    }

    [Authorize]
    public IActionResult Disconnect()
    {
        Response.Cookies.Delete("Connection");
        return RedirectToAction("Index", "Home");
    }

    [Authorize]
    [HttpGet]
    public IActionResult DemoSampleCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("CMD,OK,LSD,");

        var start = DateTime.UtcNow.AddMinutes(-40);
        var rng = new Random(42);
        for (int i = 0; i < 120; i++)
        {
            var ts = start.AddSeconds(i * 20);
            double temp = Math.Round(22.4 + Math.Sin(i / 10.0) * 1.8 + (rng.NextDouble() - 0.5) * 0.4, 2);
            double hum = Math.Round(48.5 + Math.Cos(i / 12.0) * 4.2 + (rng.NextDouble() - 0.5) * 0.8, 2);

            if (i == 45) temp = 31.8;
            if (i == 82) hum = 76.4;

            sb.AppendLine(FormattableString.Invariant(
                $"CMD,OK,{temp:F2},{hum:F2},{ts.Year},{ts.Month:D2},{ts.Day:D2},{ts.Hour:D2},{ts.Minute:D2},{ts.Second:D2}"));
        }

        return Content(sb.ToString(), "text/csv", Encoding.UTF8);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private async Task<List<string>> GetUserPortsAsync(CancellationToken ct)
    {
        string? userEmail = CurrentUserEmail;
        if (string.IsNullOrEmpty(userEmail))
        {
            return new List<string> { "No active session" };
        }

        try
        {
            var ports = await _apiClient.GetPortsAsync(userEmail, ct);
            if (ports.Count > 0)
            {
                return ports;
            }

            return new List<string> { "No COM ports detected" };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load COM ports from API for {Email}.", userEmail);
            return new List<string> { "No COM ports detected" };
        }
    }

    private bool IsBridgeAuthorized()
    {
        if (!_configuration.GetValue("BridgeSecurity:RequireBridgeKey", true))
        {
            return true;
        }

        string? expectedBridgeKey = _configuration["BridgeSecurity:BridgeKey"];
        string? expectedApiKey = _configuration["ApiSettings:ApiKey"];

        if (Request.Headers.TryGetValue("X-BRIDGE-KEY", out var bridgeHeader) &&
            FixedTimeEquals(bridgeHeader.ToString(), expectedBridgeKey))
        {
            return true;
        }

        if (Request.Headers.TryGetValue("X-API-KEY", out var apiHeader) &&
            FixedTimeEquals(apiHeader.ToString(), expectedApiKey))
        {
            return true;
        }

        return false;
    }

    private static bool FixedTimeEquals(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }
}
