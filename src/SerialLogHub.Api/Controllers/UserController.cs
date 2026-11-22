using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SerialLogHub.Api.Data;
using SerialLogHub.Api.Entities;
using SerialLogHub.Api.Models;
using SerialLogHub.Api.Services;

namespace SerialLogHub.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UserController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<UserController> _logger;
    private readonly AnomalyDetectionEngine _anomalyEngine;

    public UserController(
        AppDbContext db,
        ILogger<UserController> logger,
        AnomalyDetectionEngine? anomalyEngine = null)
    {
        _db = db;
        _logger = logger;
        _anomalyEngine = anomalyEngine ?? new AnomalyDetectionEngine();
    }

    [HttpPost("updateports")]
    public async Task<IActionResult> UpdatePorts([FromBody] UpdatePortsRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.UserEmail))
        {
            return BadRequest("UserEmail is required.");
        }

        if (!OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, request.UserEmail))
        {
            _logger.LogWarning("BOLA attempt blocked on POST /api/user/updateports for {TargetEmail}.", request.UserEmail);
            return StatusCode(StatusCodes.Status403Forbidden, "Cross-user resource access is forbidden.");
        }

        string normalizedEmail = request.UserEmail.Trim().ToLowerInvariant();
        var cleanPorts = (request.Ports ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string serializedPorts = JsonSerializer.Serialize(cleanPorts);
        var existing = await _db.UsersPort.FirstOrDefaultAsync(p => p.UserEmail.ToLower() == normalizedEmail);

        if (existing != null)
        {
            existing.Ports = serializedPorts;
        }
        else
        {
            _db.UsersPort.Add(new UserPortEntity
            {
                UserEmail = normalizedEmail,
                Ports = serializedPorts
            });
        }

        await _db.SaveChangesAsync();
        return Ok("Ports updated successfully.");
    }

    [HttpGet("getports")]
    public async Task<IActionResult> GetPorts([FromQuery] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Email parameter is required.");
        }

        if (!OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, email))
        {
            _logger.LogWarning("BOLA attempt blocked on GET /api/user/getports for {TargetEmail}.", email);
            return StatusCode(StatusCodes.Status403Forbidden, "Cross-user resource access is forbidden.");
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        var userPort = await _db.UsersPort.AsNoTracking().FirstOrDefaultAsync(p => p.UserEmail.ToLower() == normalizedEmail);

        if (userPort == null || string.IsNullOrWhiteSpace(userPort.Ports))
        {
            return NotFound("No ports found for this user.");
        }

        var ports = JsonSerializer.Deserialize<List<string>>(userPort.Ports) ?? new List<string>();
        return Ok(new { ports });
    }

    [HttpPost("SendCommand")]
    public async Task<IActionResult> SendCommand([FromBody] SendCommandRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.ResolvedPort))
        {
            return BadRequest("Email and port are required.");
        }

        if (!OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, request.Email))
        {
            _logger.LogWarning("BOLA attempt blocked on POST /api/user/SendCommand for {TargetEmail}.", request.Email);
            return StatusCode(StatusCodes.Status403Forbidden, "Cross-user command execution is forbidden.");
        }

        string normalizedEmail = request.Email.Trim().ToLowerInvariant();
        string commandId = string.IsNullOrWhiteSpace(request.CommandID)
            ? Guid.NewGuid().ToString()
            : request.CommandID.Trim();

        // Idempotency protection: if the exact CommandID already exists for this user, return the existing record
        var existingCmd = await _db.Commands
            .FirstOrDefaultAsync(c => c.Email.ToLower() == normalizedEmail && c.CommandID == commandId);
        if (existingCmd != null)
        {
            return Ok(MapToDto(existingCmd));
        }

        var command = new CommandEntity
        {
            Email = normalizedEmail,
            SelecetdPort = request.ResolvedPort.Trim(),
            BaudRate = request.BaudRate > 0 ? request.BaudRate : 9600,
            Message = request.Message ?? string.Empty,
            Timestamp = request.Timestamp == default ? DateTime.UtcNow : request.Timestamp,
            CommandID = commandId,
            IsProcessed = false,
            Status = CommandLifecycleStatus.Pending,
            Response = string.IsNullOrWhiteSpace(request.Response) ? "Pending" : request.Response,
            AttemptCount = 0
        };

        _db.Commands.Add(command);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Queued command {CommandId} ({Message}) for {Email} on {Port}.",
            command.CommandID, command.Message, command.Email, command.SelecetdPort);

        return Ok(MapToDto(command));
    }

    [HttpGet("getcommands")]
    public async Task<IActionResult> GetCommands([FromQuery] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Email parameter is required.");
        }

        if (!OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, email))
        {
            _logger.LogWarning("BOLA attempt blocked on GET /api/user/getcommands for {TargetEmail}.", email);
            return StatusCode(StatusCodes.Status403Forbidden, "Cross-user command polling is forbidden.");
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        DateTime cutoffUtc = DateTime.UtcNow.AddSeconds(-60);

        // Expire stale pending/dispatched commands older than 60s
        var staleCommands = await _db.Commands
            .Where(c => c.Email.ToLower() == normalizedEmail && !c.IsProcessed && c.Timestamp < cutoffUtc)
            .ToListAsync();

        if (staleCommands.Count > 0)
        {
            foreach (var stale in staleCommands)
            {
                stale.IsProcessed = true;
                stale.Status = CommandLifecycleStatus.TimedOut;
                stale.CompletedAt = DateTime.UtcNow;
                stale.ErrorCategory = "Expired";
                stale.Response = "Error: Command expired before bridge execution.";
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation("Expired {Count} stale command(s) for {Email}.", staleCommands.Count, normalizedEmail);
        }

        var nextCommand = await _db.Commands
            .Where(c => c.Email.ToLower() == normalizedEmail && !c.IsProcessed)
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync();

        if (nextCommand == null)
        {
            return NotFound("No pending commands.");
        }

        // Transition lifecycle: Pending -> Dispatched
        nextCommand.Status = CommandLifecycleStatus.Dispatched;
        nextCommand.DispatchedAt ??= DateTime.UtcNow;
        nextCommand.AttemptCount++;
        await _db.SaveChangesAsync();

        return Ok(MapToDto(nextCommand));
    }

    [HttpPost("UpdateCommand")]
    public async Task<IActionResult> UpdateCommand([FromBody] UpdateCommandRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest("Invalid update request.");
        }

        if (!OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, request.Email))
        {
            _logger.LogWarning("BOLA attempt blocked on POST /api/user/UpdateCommand for {TargetEmail}.", request.Email);
            return StatusCode(StatusCodes.Status403Forbidden, "Cross-user command update is forbidden.");
        }

        string normalizedEmail = request.Email.Trim().ToLowerInvariant();
        CommandEntity? command = null;

        if (!string.IsNullOrWhiteSpace(request.CommandID))
        {
            command = await _db.Commands
                .FirstOrDefaultAsync(c => c.Email.ToLower() == normalizedEmail && c.CommandID == request.CommandID);
        }

        command ??= await _db.Commands
            .Where(c => c.Email.ToLower() == normalizedEmail && !c.IsProcessed)
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync();

        if (command == null)
        {
            return NotFound("Command not found.");
        }

        if (command.IsProcessed)
        {
            return Ok(MapToDto(command));
        }

        string resp = request.Response ?? string.Empty;
        bool isErr = resp.StartsWith("Error:", StringComparison.OrdinalIgnoreCase);
        bool isTimeout = resp.Contains("timed out", StringComparison.OrdinalIgnoreCase);

        command.Response = resp;
        command.IsProcessed = true;
        command.CompletedAt = DateTime.UtcNow;
        command.Status = !string.IsNullOrWhiteSpace(request.Status)
            ? request.Status
            : isTimeout
                ? CommandLifecycleStatus.TimedOut
                : isErr
                    ? CommandLifecycleStatus.Failed
                    : CommandLifecycleStatus.Completed;

        command.ErrorCategory = !isErr ? "None" :
            isTimeout ? "Timeout" :
            resp.Contains("Could not find file", StringComparison.OrdinalIgnoreCase) ? "PortNotFound" :
            "DeviceError";

        _db.CommandHistories.Add(new CommandHistoryEntity
        {
            Email = command.Email,
            CommandID = command.CommandID,
            Port = command.SelecetdPort,
            BaudRate = command.BaudRate,
            Message = command.Message,
            Response = command.Response,
            Timestamp = DateTime.UtcNow
        });

        // Automatically persist server-side telemetry when a WLB dump succeeds
        if (!isErr &&
            command.Message.Trim().Equals("WLB", StringComparison.OrdinalIgnoreCase) &&
            resp.Contains("CMD,OK,", StringComparison.OrdinalIgnoreCase))
        {
            await PersistWlbTelemetrySafeAsync(normalizedEmail, command.SelecetdPort, resp);
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("Completed command {CommandId} for {Email} with status {Status}.",
            command.CommandID, command.Email, command.Status);
        return Ok(MapToDto(command));
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetCommandHistory([FromQuery] string email, [FromQuery] int take = 25)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Email is required.");
        }

        if (!OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, email))
        {
            _logger.LogWarning("BOLA attempt blocked on GET /api/user/history for {TargetEmail}.", email);
            return StatusCode(StatusCodes.Status403Forbidden, "Cross-user history access is forbidden.");
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        int clampedTake = Math.Clamp(take, 1, 100);

        var history = await _db.CommandHistories
            .AsNoTracking()
            .Where(h => h.Email.ToLower() == normalizedEmail)
            .OrderByDescending(h => h.Id)
            .Take(clampedTake)
            .Select(h => new
            {
                h.Id,
                h.CommandID,
                h.Port,
                h.BaudRate,
                h.Message,
                h.Timestamp,
                ResponsePreview = h.Response.Length > 120 ? h.Response.Substring(0, 120) + "..." : h.Response,
                IsError = h.Response.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
            })
            .ToListAsync();

        return Ok(history);
    }

    [HttpGet("getresponses")]
    public async Task<IActionResult> GetResponses([FromQuery] string email, [FromQuery] string CommandID)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(CommandID))
        {
            return BadRequest("Email and CommandID are required.");
        }

        if (!OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, email))
        {
            _logger.LogWarning("BOLA attempt blocked on GET /api/user/getresponses for {TargetEmail}.", email);
            return StatusCode(StatusCodes.Status403Forbidden, "Cross-user command response access is forbidden.");
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        var command = await _db.Commands
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Email.ToLower() == normalizedEmail && c.CommandID == CommandID);

        if (command == null)
        {
            return NotFound("Command not found.");
        }

        return Ok(MapToDto(command));
    }

    private async Task PersistWlbTelemetrySafeAsync(string normalizedEmail, string portName, string rawWlbResponse)
    {
        try
        {
            var samples = _anomalyEngine.ParseDataLoggerPayload(rawWlbResponse);
            if (samples.Count == 0) return;

            var device = await _db.Devices
                .FirstOrDefaultAsync(d => d.UserEmail.ToLower() == normalizedEmail && d.PortName == portName);

            if (device == null)
            {
                device = new DeviceEntity
                {
                    UserEmail = normalizedEmail,
                    PortName = portName,
                    DeviceName = $"DataLogger ({portName})",
                    SerialNumber = $"SN-{portName.Replace(" ", "")}",
                    CreatedAt = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow
                };
                _db.Devices.Add(device);
                await _db.SaveChangesAsync();
            }
            else
            {
                device.LastSeenUtc = DateTime.UtcNow;
            }

            var evaluations = _anomalyEngine.EvaluateSeries(samples);
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                var eval = evaluations[i];
                _db.TelemetryRecords.Add(new TelemetryRecordEntity
                {
                    DeviceId = device.Id,
                    UserEmail = normalizedEmail,
                    PortName = portName,
                    Temperature = s.Temperature,
                    Humidity = s.Humidity,
                    RecordedAt = s.RecordedAt,
                    IngestedAt = DateTime.UtcNow,
                    IsAnomaly = eval.IsAnomaly,
                    AnomalySeverity = eval.Severity,
                    AnomalyMethod = eval.DetectionMethod,
                    AnomalyExplanation = eval.Explanation,
                    RawPayload = s.RawPayload
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Non-fatal error persisting WLB telemetry dump for {Email}.", normalizedEmail);
        }
    }

    private static CommandResponseDto MapToDto(CommandEntity entity) => new()
    {
        Id = entity.Id,
        CommandID = entity.CommandID,
        Email = entity.Email,
        SelecetdPort = entity.SelecetdPort,
        BaudRate = entity.BaudRate,
        Message = entity.Message,
        Timestamp = entity.Timestamp,
        IsProcessed = entity.IsProcessed,
        Response = entity.Response,
        Status = entity.Status,
        DispatchedAt = entity.DispatchedAt,
        CompletedAt = entity.CompletedAt,
        ErrorCategory = entity.ErrorCategory,
        AttemptCount = entity.AttemptCount
    };
}
