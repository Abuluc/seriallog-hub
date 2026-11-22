using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SerialLogHub.Api.Data;
using SerialLogHub.Api.Entities;

namespace SerialLogHub.Api.Services;

/// <summary>
/// Optional background service active ONLY in Cloud Demo Mode (DemoMode:Enabled + DemoMode:CloudVirtualDeviceResponder).
/// Responds to queued commands targeting the virtual demo port and periodically resets demo state.
/// </summary>
public sealed class CloudDemoResponderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CloudDemoResponderService> _logger;
    private string _virtualComment = "DEMO_COLD_STORAGE_A1";
    private int _virtualIntervalSeconds = 20;
    private DateTime _lastResetUtc = DateTime.UtcNow;

    public CloudDemoResponderService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<CloudDemoResponderService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool demoEnabled = _configuration.GetValue("DemoMode:Enabled", false);
        bool cloudResponder = _configuration.GetValue("DemoMode:CloudVirtualDeviceResponder", false);
        if (!demoEnabled || !cloudResponder)
        {
            return;
        }

        string virtualPort = _configuration["DemoMode:DefaultVirtualPort"] ?? "COM99 (Virtual DataLogger - Demo)";
        string demoEmail = (_configuration["DemoMode:DemoEmail"] ?? "demo@serialloghub.local").Trim().ToLowerInvariant();
        int resetMinutes = Math.Max(5, _configuration.GetValue("DemoMode:ResetIntervalMinutes", 60));

        _logger.LogInformation("CloudDemoResponderService active for virtual port '{Port}' (reset every {Minutes}m).", virtualPort, resetMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingVirtualCommandsAsync(stoppingToken);

                if (DateTime.UtcNow - _lastResetUtc > TimeSpan.FromMinutes(resetMinutes))
                {
                    await ResetDemoStateAsync(demoEmail, virtualPort, stoppingToken);
                    _lastResetUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "CloudDemoResponderService iteration error.");
            }

            try
            {
                await Task.Delay(350, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessPendingVirtualCommandsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pending = await db.Commands
            .Where(c => !c.IsProcessed && c.SelecetdPort.Contains("COM99"))
            .OrderBy(c => c.Id)
            .Take(10)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var cmd in pending)
        {
            cmd.Response = BuildSimulatedResponse(cmd.Message);
            cmd.IsProcessed = true;

            db.CommandHistories.Add(new CommandHistoryEntity
            {
                Email = cmd.Email,
                Port = cmd.SelecetdPort,
                BaudRate = cmd.BaudRate,
                Message = cmd.Message,
                Timestamp = cmd.Timestamp,
                CommandID = cmd.CommandID,
                IsProcessed = true,
                Response = cmd.Response
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ResetDemoStateAsync(string demoEmail, string virtualPort, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var oldCommands = await db.Commands.Where(c => c.Email.ToLower() == demoEmail).ToListAsync(ct);
        var oldHistory = await db.CommandHistories.Where(c => c.Email.ToLower() == demoEmail).ToListAsync(ct);
        db.Commands.RemoveRange(oldCommands);
        db.CommandHistories.RemoveRange(oldHistory);

        var portRow = await db.UsersPort.FirstOrDefaultAsync(p => p.UserEmail.ToLower() == demoEmail, ct);
        if (portRow != null)
        {
            portRow.Ports = JsonSerializer.Serialize(new List<string> { virtualPort });
        }

        _virtualComment = "DEMO_COLD_STORAGE_A1";
        _virtualIntervalSeconds = 20;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Periodic demo data reset completed for {Email}.", demoEmail);
    }

    private string BuildSimulatedResponse(string rawCommand)
    {
        string cmd = (rawCommand ?? string.Empty).Trim();

        if (cmd.Equals("TEST", StringComparison.OrdinalIgnoreCase))
            return "CMD,OK,LSS,VIRTUAL_CLOUD_DEMO_V1";
        if (cmd.Equals("WLJ", StringComparison.OrdinalIgnoreCase))
            return "CMD,OK,BAT,87";
        if (cmd.Equals("WLW", StringComparison.OrdinalIgnoreCase))
            return $"CMD,OK,COMM,{_virtualComment}";
        if (cmd.StartsWith("WLG,", StringComparison.OrdinalIgnoreCase))
        {
            _virtualComment = cmd[4..].Trim();
            return $"CMD,OK,COMM,{_virtualComment}";
        }
        if (cmd.Equals("WLH0", StringComparison.OrdinalIgnoreCase))
        {
            var t = DateTime.UtcNow;
            return $"CMD,OK,TIME,{t.Year},{t.Month:D2},{t.Day:D2},{t.Hour:D2},{t.Minute:D2},{t.Second:D2}";
        }
        if (cmd.StartsWith("WLH1,", StringComparison.OrdinalIgnoreCase))
            return "CMD,OK,TIME_SYNCED";
        if (cmd.StartsWith("WLS0,", StringComparison.OrdinalIgnoreCase))
            return $"CMD,OK,DELAY_SET,{cmd[5..]}";
        if (cmd.Equals("WLE1", StringComparison.OrdinalIgnoreCase))
            return "CMD,OK,FORMATTED";
        if (cmd.StartsWith("WLA", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(cmd[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec) && sec >= 10)
                _virtualIntervalSeconds = sec;
            return $"CMD,OK,INTERVAL,{_virtualIntervalSeconds}";
        }
        if (cmd.Equals("WLV1", StringComparison.OrdinalIgnoreCase))
            return "CMD,OK,AUTOSTART_ENABLED";
        if (cmd.StartsWith("WLM,", StringComparison.OrdinalIgnoreCase))
            return $"CMD,OK,THRESHOLDS,{cmd[4..]}";
        if (cmd.Equals("WLB", StringComparison.OrdinalIgnoreCase))
            return GenerateSampleDump();

        return $"CMD,OK,ECHO,{cmd}";
    }

    private string GenerateSampleDump()
    {
        var sb = new StringBuilder();
        sb.AppendLine("CMD,OK,LCZ,VIRTUAL_DEMO_HEADER,Year,Month,Day,Hour,Minute,Second");
        var start = DateTime.UtcNow.AddMinutes(-20);
        var rng = new Random(101);
        for (int i = 0; i < 60; i++)
        {
            var ts = start.AddSeconds(i * _virtualIntervalSeconds);
            double temp = Math.Round(21.8 + Math.Sin(i / 6.0) * 1.4 + (rng.NextDouble() - 0.5) * 0.3, 2);
            double hum = Math.Round(51.0 + Math.Cos(i / 8.0) * 3.5 + (rng.NextDouble() - 0.5) * 0.6, 2);
            if (i == 28) temp = 29.4;
            sb.AppendLine(FormattableString.Invariant(
                $"CMD,OK,{temp:F2},{hum:F2},{ts.Year},{ts.Month:D2},{ts.Day:D2},{ts.Hour:D2},{ts.Minute:D2},{ts.Second:D2}"));
        }
        sb.Append("CMD,OK,LSD,");
        return sb.ToString();
    }
}
