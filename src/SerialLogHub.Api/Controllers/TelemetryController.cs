using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SerialLogHub.Api.Data;
using SerialLogHub.Api.Entities;
using SerialLogHub.Api.Services;

namespace SerialLogHub.Api.Controllers;

public sealed class TelemetryIngestRequest
{
    public string Email { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? SerialNumber { get; set; }
    public string? RawCsvPayload { get; set; }
    public List<TelemetryInputSample>? Samples { get; set; }
    public AnomalyDetectionConfig? Config { get; set; }
}

public sealed class TelemetryAnalyzeRequest
{
    public string? RawCsvPayload { get; set; }
    public List<TelemetryInputSample>? Samples { get; set; }
    public AnomalyDetectionConfig? Config { get; set; }
}

[ApiController]
[Route("api/[controller]")]
public class TelemetryController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AnomalyDetectionEngine _anomalyEngine;
    private readonly ILogger<TelemetryController> _logger;

    public TelemetryController(
        AppDbContext db,
        AnomalyDetectionEngine anomalyEngine,
        ILogger<TelemetryController> logger)
    {
        _db = db;
        _anomalyEngine = anomalyEngine;
        _logger = logger;
    }

    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices([FromQuery] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Email is required.");
        }

        if (!OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, email))
        {
            _logger.LogWarning("BOLA attempt blocked on GET /api/telemetry/devices for {TargetEmail}.", email);
            return StatusCode(StatusCodes.Status403Forbidden, "Cross-user resource access is forbidden.");
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        var devices = await _db.Devices
            .AsNoTracking()
            .Where(d => d.UserEmail.ToLower() == normalizedEmail)
            .OrderBy(d => d.PortName)
            .Select(d => new
            {
                d.Id,
                d.DeviceName,
                d.PortName,
                d.SerialNumber,
                d.CreatedAt,
                d.LastSeenUtc,
                TotalRecords = d.TelemetryRecords.Count(),
                AnomalyRecords = d.TelemetryRecords.Count(r => r.IsAnomaly)
            })
            .ToListAsync();

        return Ok(devices);
    }

    [HttpPost("ingest")]
    public async Task<IActionResult> IngestTelemetry([FromBody] TelemetryIngestRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.PortName))
        {
            return BadRequest("Email and PortName are required.");
        }

        if (!OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, request.Email))
        {
            _logger.LogWarning("BOLA attempt blocked on POST /api/telemetry/ingest for {TargetEmail}.", request.Email);
            return StatusCode(StatusCodes.Status403Forbidden, "Cross-user resource access is forbidden.");
        }

        string normalizedEmail = request.Email.Trim().ToLowerInvariant();
        string cleanPort = request.PortName.Trim();

        List<TelemetryInputSample> samples = request.Samples ?? new List<TelemetryInputSample>();
        if (samples.Count == 0 && !string.IsNullOrWhiteSpace(request.RawCsvPayload))
        {
            samples = _anomalyEngine.ParseDataLoggerPayload(request.RawCsvPayload);
        }

        if (samples.Count == 0)
        {
            return BadRequest("No valid DataLogger telemetry samples found in payload.");
        }

        var device = await _db.Devices
            .FirstOrDefaultAsync(d => d.UserEmail.ToLower() == normalizedEmail && d.PortName == cleanPort);

        if (device == null)
        {
            device = new DeviceEntity
            {
                UserEmail = normalizedEmail,
                PortName = cleanPort,
                DeviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? $"DataLogger ({cleanPort})" : request.DeviceName.Trim(),
                SerialNumber = string.IsNullOrWhiteSpace(request.SerialNumber) ? $"SN-{cleanPort.Replace(" ", "")}" : request.SerialNumber.Trim(),
                CreatedAt = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow
            };
            _db.Devices.Add(device);
            await _db.SaveChangesAsync();
        }
        else
        {
            device.LastSeenUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(request.DeviceName))
            {
                device.DeviceName = request.DeviceName.Trim();
            }
        }

        var evaluations = _anomalyEngine.EvaluateSeries(samples, request.Config);
        var existingTimestamps = (await _db.TelemetryRecords
            .AsNoTracking()
            .Where(r => r.DeviceId == device.Id)
            .Select(r => r.RecordedAt)
            .ToListAsync())
            .ToHashSet();

        int inserted = 0;
        int anomalyCount = 0;

        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var eval = evaluations[i];
            if (eval.IsAnomaly) anomalyCount++;

            if (existingTimestamps.Contains(s.RecordedAt))
            {
                continue;
            }

            existingTimestamps.Add(s.RecordedAt);
            _db.TelemetryRecords.Add(new TelemetryRecordEntity
            {
                DeviceId = device.Id,
                UserEmail = normalizedEmail,
                PortName = cleanPort,
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
            inserted++;
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("Ingested {Inserted} new telemetry rows ({Anomalies} anomalies) for {Email} device {DeviceId}.",
            inserted, anomalyCount, normalizedEmail, device.Id);

        return Ok(new
        {
            deviceId = device.Id,
            deviceName = device.DeviceName,
            portName = device.PortName,
            totalEvaluated = samples.Count,
            insertedCount = inserted,
            anomalyCount,
            evaluations
        });
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] string email,
        [FromQuery] int? deviceId = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] bool anomaliesOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 250)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Email is required.");
        }

        if (!OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, email))
        {
            _logger.LogWarning("BOLA attempt blocked on GET /api/telemetry/history for {TargetEmail}.", email);
            return StatusCode(StatusCodes.Status403Forbidden, "Cross-user resource access is forbidden.");
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        int clampedPage = Math.Max(1, page);
        int clampedSize = Math.Clamp(pageSize, 1, 1000);

        var query = _db.TelemetryRecords
            .AsNoTracking()
            .Where(r => r.UserEmail.ToLower() == normalizedEmail);

        if (deviceId.HasValue)
        {
            query = query.Where(r => r.DeviceId == deviceId.Value);
        }

        if (fromUtc.HasValue)
        {
            query = query.Where(r => r.RecordedAt >= fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            query = query.Where(r => r.RecordedAt <= toUtc.Value);
        }

        if (anomaliesOnly)
        {
            query = query.Where(r => r.IsAnomaly);
        }

        int totalCount = await query.CountAsync();
        int anomalyCount = await query.CountAsync(r => r.IsAnomaly);

        var records = await query
            .OrderBy(r => r.RecordedAt)
            .Skip((clampedPage - 1) * clampedSize)
            .Take(clampedSize)
            .Select(r => new
            {
                r.Id,
                r.DeviceId,
                r.PortName,
                r.Temperature,
                r.Humidity,
                r.RecordedAt,
                r.IsAnomaly,
                r.AnomalySeverity,
                r.AnomalyMethod,
                r.AnomalyExplanation
            })
            .ToListAsync();

        double minTemp = records.Count > 0 ? records.Min(r => r.Temperature) : 0;
        double maxTemp = records.Count > 0 ? records.Max(r => r.Temperature) : 0;
        double avgTemp = records.Count > 0 ? Math.Round(records.Average(r => r.Temperature), 2) : 0;
        double minHum = records.Count > 0 ? records.Min(r => r.Humidity) : 0;
        double maxHum = records.Count > 0 ? records.Max(r => r.Humidity) : 0;
        double avgHum = records.Count > 0 ? Math.Round(records.Average(r => r.Humidity), 2) : 0;

        return Ok(new
        {
            totalCount,
            anomalyCount,
            page = clampedPage,
            pageSize = clampedSize,
            stats = new { minTemp, avgTemp, maxTemp, minHum, avgHum, maxHum },
            records
        });
    }

    [HttpGet("compare")]
    public async Task<IActionResult> CompareDevices(
        [FromQuery] string email,
        [FromQuery] int deviceA,
        [FromQuery] int deviceB)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Email is required.");
        }

        if (!OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, email))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Cross-user resource access is forbidden.");
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        var devA = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceA && d.UserEmail.ToLower() == normalizedEmail);
        var devB = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceB && d.UserEmail.ToLower() == normalizedEmail);

        if (devA == null || devB == null)
        {
            return NotFound("One or both devices were not found for this operator.");
        }

        async Task<object> BuildSummaryAsync(DeviceEntity dev)
        {
            var rows = await _db.TelemetryRecords
                .AsNoTracking()
                .Where(r => r.DeviceId == dev.Id)
                .ToListAsync();

            int count = rows.Count;
            int anomalies = rows.Count(r => r.IsAnomaly);
            return new
            {
                deviceId = dev.Id,
                deviceName = dev.DeviceName,
                portName = dev.PortName,
                serialNumber = dev.SerialNumber,
                sampleCount = count,
                anomalyCount = anomalies,
                anomalyRatePercent = count > 0 ? Math.Round((anomalies * 100.0) / count, 2) : 0,
                minTemp = count > 0 ? rows.Min(r => r.Temperature) : 0,
                avgTemp = count > 0 ? Math.Round(rows.Average(r => r.Temperature), 2) : 0,
                maxTemp = count > 0 ? rows.Max(r => r.Temperature) : 0,
                minHum = count > 0 ? rows.Min(r => r.Humidity) : 0,
                avgHum = count > 0 ? Math.Round(rows.Average(r => r.Humidity), 2) : 0,
                maxHum = count > 0 ? rows.Max(r => r.Humidity) : 0
            };
        }

        return Ok(new
        {
            deviceA = await BuildSummaryAsync(devA),
            deviceB = await BuildSummaryAsync(devB)
        });
    }

    [HttpPost("analyze")]
    public IActionResult AnalyzeSeries([FromBody] TelemetryAnalyzeRequest request)
    {
        if (request == null)
        {
            return BadRequest("Analyze request payload is required.");
        }

        List<TelemetryInputSample> samples = request.Samples ?? new List<TelemetryInputSample>();
        if (samples.Count == 0 && !string.IsNullOrWhiteSpace(request.RawCsvPayload))
        {
            samples = _anomalyEngine.ParseDataLoggerPayload(request.RawCsvPayload);
        }

        if (samples.Count == 0)
        {
            return BadRequest("No valid telemetry samples provided.");
        }

        var evaluations = _anomalyEngine.EvaluateSeries(samples, request.Config);
        int anomalies = evaluations.Count(e => e.IsAnomaly);

        return Ok(new
        {
            totalSamples = evaluations.Count,
            anomalyCount = anomalies,
            evaluations
        });
    }
}
