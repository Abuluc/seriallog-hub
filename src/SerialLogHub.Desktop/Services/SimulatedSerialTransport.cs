using System.Globalization;
using System.Text;

namespace SerialLogHub.Desktop.Services;

/// <summary>
/// Standalone Virtual DataLogger Simulator for hardware-free portfolio demonstrations.
/// Responds to the exact hardware command protocol without touching physical COM ports.
/// </summary>
public sealed class SimulatedSerialTransport : ISerialTransport
{
    private readonly string _virtualPortName;
    private string _deviceComment = "DEMO_COLD_STORAGE_A1";
    private int _batteryPercent = 87;
    private int _intervalSeconds = 20;
    private DateTime _deviceClockUtc = DateTime.UtcNow;

    public SimulatedSerialTransport(string virtualPortName = "COM99 (Virtual DataLogger - Demo)")
    {
        _virtualPortName = virtualPortName;
    }

    public string VirtualPortName => _virtualPortName;

    public bool IsVirtualPort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return false;
        }

        string cleanInput = RealSerialTransport.ExtractRawPortName(portName);
        string cleanVirtual = RealSerialTransport.ExtractRawPortName(_virtualPortName);
        return string.Equals(cleanInput, cleanVirtual, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> GetAvailablePorts() => new[] { _virtualPortName };

    public async Task<string> ExecuteCommandAsync(
        string portName,
        int baudRate,
        string commandMessage,
        Func<int, bool, string, Task>? onStreamProgress = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(120, cancellationToken);
        string cmd = (commandMessage ?? string.Empty).Trim();

        if (cmd.Equals("TEST", StringComparison.OrdinalIgnoreCase))
        {
            return "CMD,OK,LSS,VIRTUAL_LOGGER_V1";
        }

        if (cmd.Equals("WLB", StringComparison.OrdinalIgnoreCase))
        {
            if (onStreamProgress != null)
            {
                await onStreamProgress(0, false, string.Empty);
            }

            string payload = GenerateSimulatedTelemetryDump();

            if (onStreamProgress != null)
            {
                await onStreamProgress(60, true, payload);
            }

            return payload;
        }

        if (cmd.Equals("WLJ", StringComparison.OrdinalIgnoreCase))
        {
            return $"CMD,OK,BAT,{_batteryPercent}";
        }

        if (cmd.Equals("WLW", StringComparison.OrdinalIgnoreCase))
        {
            return $"CMD,OK,COMM,{_deviceComment}";
        }

        if (cmd.StartsWith("WLG,", StringComparison.OrdinalIgnoreCase))
        {
            _deviceComment = cmd[4..].Trim();
            return $"CMD,OK,COMM,{_deviceComment}";
        }

        if (cmd.Equals("WLH0", StringComparison.OrdinalIgnoreCase))
        {
            var t = _deviceClockUtc;
            return $"CMD,OK,TIME,{t.Year},{t.Month:D2},{t.Day:D2},{t.Hour:D2},{t.Minute:D2},{t.Second:D2}";
        }

        if (cmd.StartsWith("WLH1,", StringComparison.OrdinalIgnoreCase))
        {
            _deviceClockUtc = DateTime.UtcNow;
            return "CMD,OK,TIME_SYNCED";
        }

        if (cmd.StartsWith("WLS0,", StringComparison.OrdinalIgnoreCase))
        {
            return $"CMD,OK,DELAY_SET,{cmd[5..]}";
        }

        if (cmd.Equals("WLE1", StringComparison.OrdinalIgnoreCase))
        {
            return "CMD,OK,FORMATTED";
        }

        if (cmd.StartsWith("WLA", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(cmd[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) && seconds >= 10)
            {
                _intervalSeconds = seconds;
            }

            return $"CMD,OK,INTERVAL,{_intervalSeconds}";
        }

        if (cmd.Equals("WLV1", StringComparison.OrdinalIgnoreCase))
        {
            return "CMD,OK,AUTOSTART_ENABLED";
        }

        if (cmd.StartsWith("WLM,", StringComparison.OrdinalIgnoreCase))
        {
            return $"CMD,OK,THRESHOLDS,{cmd[4..]}";
        }

        return $"CMD,OK,ECHO,{cmd}";
    }

    private string GenerateSimulatedTelemetryDump()
    {
        var sb = new StringBuilder();
        sb.AppendLine("CMD,OK,LCZ,VIRTUAL_DEMO_HEADER,Year,Month,Day,Hour,Minute,Second");

        var start = DateTime.UtcNow.AddMinutes(-20);
        var rng = new Random(101);
        for (int i = 0; i < 60; i++)
        {
            var ts = start.AddSeconds(i * _intervalSeconds);
            double temp = Math.Round(21.8 + Math.Sin(i / 6.0) * 1.4 + (rng.NextDouble() - 0.5) * 0.3, 2);
            double hum = Math.Round(51.0 + Math.Cos(i / 8.0) * 3.5 + (rng.NextDouble() - 0.5) * 0.6, 2);

            if (i == 28) temp = 29.4; // Sample temperature spike

            sb.AppendLine(FormattableString.Invariant(
                $"CMD,OK,{temp:F2},{hum:F2},{ts.Year},{ts.Month:D2},{ts.Day:D2},{ts.Hour:D2},{ts.Minute:D2},{ts.Second:D2}"));
        }

        sb.Append("CMD,OK,LSD,");
        return sb.ToString();
    }
}
