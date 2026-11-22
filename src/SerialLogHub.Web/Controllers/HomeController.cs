using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using SerialLogHub.Web.Hubs;
using SerialLogHub.Web.Models;
using SerialLogHub.Web.Services;

namespace SerialLogHub.Web.Controllers;

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
    public IActionResult CrateTable() => View();

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

    /// <summary>
    /// Webhook called by the Desktop COM Port Bridge to report online status.
    /// Protected by X-BRIDGE-KEY (or X-API-KEY) header.
    /// </summary>
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
            var connectionId = _connectionManager.GetConnectionId(payload.UserName);
            if (!string.IsNullOrEmpty(connectionId))
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("ReceiveMessage", payload.AppName, payload.Message, ct);
                return Ok("Data sent to the user.");
            }

            return NotFound("User not connected.");
        }

        return BadRequest("Invalid app name.");
    }

    /// <summary>
    /// Webhook called by the Desktop COM Port Bridge to stream serial log reading progress and final payload.
    /// Protected by X-BRIDGE-KEY (or X-API-KEY) header.
    /// </summary>
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
            var connectionId = _connectionManager.GetConnectionId(payload.UserName);
            if (!string.IsNullOrEmpty(connectionId))
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("datareadding", payload.AppName, payload.Message, payload.Data, ct);
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

        if (model == null || string.IsNullOrWhiteSpace(model.SelectedPort) || string.IsNullOrWhiteSpace(model.MessageToSend))
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
            Response = "Response yok"
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

        if (model == null || string.IsNullOrWhiteSpace(model.SelectedPort))
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
            Response = "Response yok"
        };

        var response = await _apiClient.SendAndWaitForResponseAsync(command, ct);
        if (response == null || string.IsNullOrWhiteSpace(response.Response))
        {
            return Json(new
            {
                status = "Error",
                message = "Connection failed. Ensure the Desktop COM Bridge is running and the device is connected.",
                ResponseMessage = response
            });
        }

        string lower = response.Response.ToLowerInvariant();
        if (lower.Contains("cmd,ok,lcz,") || lower.Contains("cmd,ok,lss,") || lower.Contains("cmd,ok,"))
        {
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
        }

        return Json(new { status = "success", message = "Connected.", ResponseMessage = response });
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Getavabileport(CancellationToken ct)
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
            Response = "Response yok"
        };

        var response = await _apiClient.SendAndWaitForResponseAsync(command, ct);
        return Json(new
        {
            status = response != null ? "success" : "timeout",
            message = response != null ? "Delayed start configured." : "Timed out waiting for device.",
            Result = response?.Response
        });
    }

    [Authorize]
    [HttpGet("Home/GetCookie")]
    public IActionResult GetCookie(string cookieheader)
    {
        if (string.Equals(cookieheader, "Connection", StringComparison.OrdinalIgnoreCase) &&
            Request.Cookies.TryGetValue("Connection", out var cookieValue))
        {
            return Json(new { success = true, value = cookieValue });
        }

        return Json(new { success = false, message = "Cookie not found." });
    }

    [Authorize]
    [HttpGet("Home/Disconnect")]
    public IActionResult Disconnect()
    {
        Response.Cookies.Delete("Connection");
        return Ok(new { status = "disconnected" });
    }

    /// <summary>
    /// Generates clearly labeled synthetic sample DataLogger CSV records in Demo Mode
    /// matching the real hardware column format (row[2]=Temp, row[3]=Humidity, row[4..9]=Y,M,D,h,m,s).
    /// </summary>
    [Authorize]
    [HttpGet("Home/DemoSampleCsv")]
    public IActionResult DemoSampleCsv()
    {
        if (!_configuration.GetValue("DemoMode:Enabled", false))
        {
            return NotFound("Demo mode is disabled.");
        }

        var sb = new StringBuilder();
        sb.AppendLine("CMD,DATA,Temp,Humidity,Year,Month,Day,Hour,Minute,Second");

        var start = DateTime.UtcNow.AddMinutes(-40);
        var rng = new Random(42);
        for (int i = 0; i < 120; i++)
        {
            var ts = start.AddSeconds(i * 20);
            double temp = Math.Round(22.4 + Math.Sin(i / 10.0) * 1.8 + (rng.NextDouble() - 0.5) * 0.4, 2);
            double hum = Math.Round(48.5 + Math.Cos(i / 12.0) * 4.2 + (rng.NextDouble() - 0.5) * 0.8, 2);

            // Inject 2 realistic sensor spike anomalies so the anomaly filter can be demonstrated
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
            !string.IsNullOrWhiteSpace(expectedBridgeKey) &&
            string.Equals(bridgeHeader.ToString(), expectedBridgeKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (Request.Headers.TryGetValue("X-API-KEY", out var apiHeader) &&
            !string.IsNullOrWhiteSpace(expectedApiKey) &&
            string.Equals(apiHeader.ToString(), expectedApiKey, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}
