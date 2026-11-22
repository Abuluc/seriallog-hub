using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SerialLogHub.Web.Models;
using SerialLogHub.Web.Services;

namespace SerialLogHub.Web.Controllers;

[Authorize]
public class SettingsController : Controller
{
    private readonly ApiClientService _apiClient;

    public SettingsController(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    private string? CurrentUserEmail => User.Identity?.Name;

    public IActionResult Index() => View();
    public IActionResult Time() => RedirectToAction(nameof(Index));
    public IActionResult Calibration() => RedirectToAction(nameof(Index));
    public IActionResult AddComm() => RedirectToAction(nameof(Index));
    public IActionResult DelayStart() => RedirectToAction(nameof(Index));
    public IActionResult TempHum() => RedirectToAction(nameof(Index));
    public IActionResult Formatting() => RedirectToAction(nameof(Index));

    [HttpPost]
    public Task<JsonResult> SetDelayStart(CalibrationModel model, string Time, CancellationToken ct)
        => DispatchDeviceCommandAsync(model, $"WLS0,{Time}", ct);

    [HttpPost]
    public Task<JsonResult> SetDelayEnd(CalibrationModel model, string Time, CancellationToken ct)
        => DispatchDeviceCommandAsync(model, $"WLS0,{Time}", ct);

    [HttpPost]
    public Task<JsonResult> SetTime(CalibrationModel model, string Time, CancellationToken ct)
        => DispatchDeviceCommandAsync(model, $"WLA{Time}", ct);

    [HttpPost("Settings/SetComment")]
    [HttpPost("Settings/Settocomment")]
    public Task<JsonResult> SetComment(CalibrationModel model, string Comment, CancellationToken ct)
        => DispatchDeviceCommandAsync(model, $"WLG, {Comment}", ct);

    [HttpPost]
    public Task<JsonResult> AutoStart(CalibrationModel model, CancellationToken ct)
        => DispatchDeviceCommandAsync(model, "WLV1", ct);

    [HttpGet]
    public Task<JsonResult> GetBattery(CalibrationModel model, CancellationToken ct)
        => DispatchDeviceCommandAsync(model, "WLJ", ct);

    [HttpPost]
    public Task<JsonResult> Format(CalibrationModel model, CancellationToken ct)
        => DispatchDeviceCommandAsync(model, "WLE1", ct);

    [HttpGet("Settings/GetComm")]
    public Task<JsonResult> GetComm(CalibrationModel model, CancellationToken ct)
        => DispatchDeviceCommandAsync(model, "WLW", ct);

    [HttpPost]
    public Task<JsonResult> UpdateDeviceTime(CalibrationModel model, string deviceTime, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceTime))
        {
            return Task.FromResult(Json(new { status = "error", message = "Valid device time is required." }));
        }

        return DispatchDeviceCommandAsync(model, $"WLH1,{deviceTime}", ct);
    }

    [HttpPost]
    public Task<JsonResult> GetCalibrationTime(CalibrationModel model, CancellationToken ct)
        => DispatchDeviceCommandAsync(model, "WLH0", ct);

    [HttpPost]
    public Task<JsonResult> SetMM(CalibrationModel model, ThresholdModel mmModel, CancellationToken ct)
    {
        if (mmModel == null)
        {
            return Task.FromResult(Json(new { status = "error", message = "Threshold values are required." }));
        }

        if (mmModel.MinTemp >= mmModel.MaxTemp)
        {
            return Task.FromResult(Json(new { status = "error", message = "Minimum temperature must be lower than maximum temperature." }));
        }

        if (mmModel.MinHum < 0 || mmModel.MaxHum > 100 || mmModel.MinHum >= mmModel.MaxHum)
        {
            return Task.FromResult(Json(new { status = "error", message = "Humidity thresholds must satisfy 0 <= Min < Max <= 100." }));
        }

        string command = $"WLM,{mmModel.MinTemp},{mmModel.MaxTemp},{mmModel.MinHum},{mmModel.MaxHum}";
        return DispatchDeviceCommandAsync(model, command, ct);
    }

    private async Task<JsonResult> DispatchDeviceCommandAsync(CalibrationModel model, string commandMessage, CancellationToken ct)
    {
        string? userEmail = CurrentUserEmail;
        if (string.IsNullOrEmpty(userEmail))
        {
            return Json(new { status = "error", message = "User session not found." });
        }

        if (model == null || string.IsNullOrWhiteSpace(model.SelectedPort))
        {
            return Json(new { status = "error", message = "Please select a valid port." });
        }

        var command = new CommandModel
        {
            Email = userEmail,
            SelecetdPort = model.SelectedPort,
            BaudRate = model.SelectedBaudRate > 0 ? model.SelectedBaudRate : 9600,
            Message = commandMessage,
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
                message = "No response received from device within timeout.",
                Response = string.Empty,
                ResponseMessage = string.Empty,
                Result = string.Empty
            });
        }

        if (!string.IsNullOrWhiteSpace(result.Response) &&
            result.Response.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            return Json(new
            {
                status = "error",
                message = result.Response,
                Response = result.Response,
                ResponseMessage = result.Response,
                Result = result.Response
            });
        }

        return Json(new
        {
            status = "success",
            message = "Command executed.",
            Response = result.Response,
            ResponseMessage = result.Response,
            Result = result.Response
        });
    }
}
