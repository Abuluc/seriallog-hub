using System.IO.Ports;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace SerialLogHub.Desktop.Services;

/// <summary>
/// Preserves the exact hardware serial protocol used by physical DataLogger devices:
/// - Handshake ("TEST" -> sends "WLB", reads single response line)
/// - Bulk log dump ("WLB" -> streams until "CMD,OK,LSD," or 5-second inactivity timeout)
/// - Device configuration commands ("WLJ", "WLW", "WLG", "WLH0", "WLH1", "WLS0", "WLE1", "WLA", "WLV1", "WLM")
/// Includes safe WMI friendly port description lookup ported from WebPortApp-main.
/// </summary>
public sealed class RealSerialTransport : ISerialTransport
{
    private const string EndOfStreamMarker = "CMD,OK,LSD,";
    private const int ReadBufferSize = 4096;
    private const int MaxStreamLineCount = 100_000;
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex ValidComPortRegex = new(@"^COM\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<string> GetAvailablePorts()
    {
        string[] rawPorts = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToArray();
        if (rawPorts.Length == 0)
        {
            return Array.Empty<string>();
        }

        var descriptions = GetWmiPortDescriptions();
        var result = new List<string>(rawPorts.Length);

        foreach (string port in rawPorts)
        {
            if (descriptions.TryGetValue(port, out string? description) && !string.IsNullOrWhiteSpace(description))
            {
                result.Add($"{port} ({description})");
            }
            else
            {
                result.Add(port);
            }
        }

        return result;
    }

    public async Task<string> ExecuteCommandAsync(
        string portName,
        int baudRate,
        string commandMessage,
        Func<int, bool, string, Task>? onStreamProgress = null,
        CancellationToken cancellationToken = default)
    {
        string cleanPort = ExtractRawPortName(portName);
        int effectiveBaud = baudRate > 0 ? baudRate : 9600;

        using var serialPort = new SerialPort(cleanPort, effectiveBaud)
        {
            WriteTimeout = 3000,
            ReadTimeout = 5000
        };

        serialPort.Open();

        if (serialPort.BytesToRead > 0)
        {
            serialPort.DiscardInBuffer();
        }

        if (string.Equals(commandMessage, "TEST", StringComparison.OrdinalIgnoreCase))
        {
            serialPort.WriteLine("WLB");
            await Task.Delay(200, cancellationToken);
            return await Task.Run(serialPort.ReadLine, cancellationToken);
        }

        if (string.Equals(commandMessage, "WLB", StringComparison.OrdinalIgnoreCase))
        {
            serialPort.WriteLine(commandMessage);
            await Task.Delay(200, cancellationToken);
            return await ReadBulkStreamAsync(serialPort, onStreamProgress, cancellationToken);
        }

        serialPort.WriteLine(commandMessage);
        return await Task.Run(serialPort.ReadLine, cancellationToken);
    }

    private static async Task<string> ReadBulkStreamAsync(
        SerialPort serialPort,
        Func<int, bool, string, Task>? onStreamProgress,
        CancellationToken cancellationToken)
    {
        var dataBuilder = new StringBuilder();
        var accumulated = new StringBuilder();
        byte[] buffer = new byte[ReadBufferSize];
        int validLineCount = 0;
        DateTime lastDataReceived = DateTime.UtcNow;

        if (onStreamProgress != null)
        {
            await onStreamProgress(0, false, string.Empty);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (serialPort.BytesToRead == 0)
            {
                if (DateTime.UtcNow - lastDataReceived > InactivityTimeout)
                {
                    break;
                }

                await Task.Delay(100, cancellationToken);
                continue;
            }

            int bytesRead = await serialPort.BaseStream.ReadAsync(buffer.AsMemory(0, ReadBufferSize), cancellationToken);
            if (bytesRead <= 0)
            {
                continue;
            }

            lastDataReceived = DateTime.UtcNow;
            string chunk = Encoding.ASCII.GetString(buffer, 0, bytesRead);
            accumulated.Append(chunk);
            validLineCount += CountLines(chunk);

            string allData = accumulated.ToString();
            int endIndex = allData.IndexOf(EndOfStreamMarker, StringComparison.Ordinal);
            if (endIndex >= 0)
            {
                dataBuilder.Append(allData, 0, endIndex + EndOfStreamMarker.Length);
                break;
            }

            if (validLineCount >= MaxStreamLineCount)
            {
                dataBuilder.Append(allData);
                break;
            }

            if (onStreamProgress != null && validLineCount % 20 == 0)
            {
                await onStreamProgress(validLineCount, false, string.Empty);
            }
        }

        if (dataBuilder.Length == 0)
        {
            dataBuilder.Append(accumulated);
        }

        string finalPayload = dataBuilder.ToString();
        if (onStreamProgress != null)
        {
            await onStreamProgress(validLineCount, true, finalPayload);
        }

        return finalPayload;
    }

    public static string ExtractRawPortName(string portDisplay)
    {
        if (string.IsNullOrWhiteSpace(portDisplay))
        {
            return string.Empty;
        }

        string candidate = portDisplay.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return ValidComPortRegex.IsMatch(candidate) ? candidate.ToUpperInvariant() : portDisplay.Trim();
    }

    private static Dictionary<string, string> GetWmiPortDescriptions()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID, Description FROM Win32_SerialPort");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                string? deviceId = obj["DeviceID"]?.ToString();
                string? description = obj["Description"]?.ToString();
                if (!string.IsNullOrWhiteSpace(deviceId) && !string.IsNullOrWhiteSpace(description))
                {
                    map[deviceId] = description;
                }
            }
        }
        catch
        {
            // WMI may be unavailable in restricted environments; fall back to raw COM port names.
        }

        return map;
    }

    private static int CountLines(string text)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf('\n', index)) != -1)
        {
            index++;
            count++;
        }

        return count;
    }
}
