using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SerialLogHub.Api.Data;
using SerialLogHub.Api.Entities;
using SerialLogHub.Api.Models;

namespace SerialLogHub.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UserController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<UserController> _logger;

    public UserController(AppDbContext db, ILogger<UserController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpPost("updateports")]
    public async Task<IActionResult> UpdatePorts([FromBody] UpdatePortsRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.UserEmail))
        {
            return BadRequest("UserEmail is required.");
        }

        string normalizedEmail = request.UserEmail.Trim().ToLowerInvariant();
        var distinctPorts = (request.Ports ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .ToList();

        string serialized = JsonSerializer.Serialize(distinctPorts);

        var existing = await _db.UsersPort.FirstOrDefaultAsync(p => p.UserEmail.ToLower() == normalizedEmail);
        if (existing == null)
        {
            _db.UsersPort.Add(new UserPortEntity
            {
                UserEmail = normalizedEmail,
                Ports = serialized
            });
        }
        else
        {
            existing.Ports = serialized;
        }

        await _db.SaveChangesAsync();
        return Ok(new { message = "Ports updated.", ports = distinctPorts });
    }

    [HttpGet("getports")]
    public async Task<IActionResult> GetPorts([FromQuery] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Email is required.");
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        var record = await _db.UsersPort.AsNoTracking().FirstOrDefaultAsync(p => p.UserEmail.ToLower() == normalizedEmail);

        List<string> ports = new();
        if (record != null && !string.IsNullOrWhiteSpace(record.Ports))
        {
            try
            {
                ports = JsonSerializer.Deserialize<List<string>>(record.Ports) ?? new List<string>();
            }
            catch
            {
                ports = record.Ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
        }

        return Ok(new GetPortsResponse { Ports = ports });
    }

    [HttpPost("SendCommand")]
    public async Task<IActionResult> SendCommand([FromBody] SendCommandRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.ResolvedPort))
        {
            return BadRequest("Email and port are required.");
        }

        string normalizedEmail = request.Email.Trim().ToLowerInvariant();
        string commandId = string.IsNullOrWhiteSpace(request.CommandID)
            ? Guid.NewGuid().ToString()
            : request.CommandID.Trim();

        var command = new CommandEntity
        {
            Email = normalizedEmail,
            SelecetdPort = request.ResolvedPort.Trim(),
            BaudRate = request.BaudRate > 0 ? request.BaudRate : 9600,
            Message = request.Message ?? string.Empty,
            Timestamp = request.Timestamp == default ? DateTime.UtcNow : request.Timestamp,
            CommandID = commandId,
            IsProcessed = false,
            Response = string.IsNullOrWhiteSpace(request.Response) ? "Pending" : request.Response
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
            return BadRequest("Email is required.");
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        DateTime expirationCutoff = DateTime.UtcNow.AddSeconds(-60);

        // Auto-expire stale unprocessed commands so dead-port commands never block later requests
        var staleCommands = await _db.Commands
            .Where(c => c.Email.ToLower() == normalizedEmail && !c.IsProcessed && c.Timestamp < expirationCutoff)
            .ToListAsync();

        if (staleCommands.Count > 0)
        {
            foreach (var stale in staleCommands)
            {
                stale.IsProcessed = true;
                stale.Response = "Error: Command expired before bridge execution.";
            }

            await _db.SaveChangesAsync();
        }

        var pending = await _db.Commands
            .AsNoTracking()
            .Where(c => c.Email.ToLower() == normalizedEmail && !c.IsProcessed)
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync();

        if (pending == null)
        {
            return NotFound("No pending commands.");
        }

        return Ok(MapToDto(pending));
    }

    [HttpPost("UpdateCommand")]
    public async Task<IActionResult> UpdateCommand([FromBody] UpdateCommandRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest("Email is required.");
        }

        string normalizedEmail = request.Email.Trim().ToLowerInvariant();

        CommandEntity? command;
        if (!string.IsNullOrWhiteSpace(request.CommandID))
        {
            command = await _db.Commands
                .FirstOrDefaultAsync(c => c.Email.ToLower() == normalizedEmail && c.CommandID == request.CommandID);
        }
        else
        {
            command = await _db.Commands
                .Where(c => c.Email.ToLower() == normalizedEmail && !c.IsProcessed)
                .OrderBy(c => c.Id)
                .FirstOrDefaultAsync();
        }

        if (command == null)
        {
            return NotFound("No matching pending command found.");
        }

        // Prevent duplicate history entries or overwriting already-processed commands
        if (command.IsProcessed)
        {
            return Ok(MapToDto(command));
        }

        command.Response = request.Response ?? string.Empty;
        command.IsProcessed = true;

        _db.CommandHistories.Add(new CommandHistoryEntity
        {
            Email = command.Email,
            Port = command.SelecetdPort,
            BaudRate = command.BaudRate,
            Message = command.Message,
            Timestamp = command.Timestamp,
            CommandID = command.CommandID,
            IsProcessed = true,
            Response = command.Response
        });

        await _db.SaveChangesAsync();

        _logger.LogInformation("Completed command {CommandId} for {Email}.", command.CommandID, command.Email);
        return Ok(MapToDto(command));
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetCommandHistory([FromQuery] string email, [FromQuery] int take = 25)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Email is required.");
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
        Response = entity.Response
    };
}
