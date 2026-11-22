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
/// All blocking SerialPort / WMI operations run off the UI thread.
/// </summary>
public sealed class RealSerialTransport : ISerialTransport
{
    private const string EndOfStreamMarker = "CMD,OK,LSD,";
    private const int ReadBufferSize = 4096;
    private const int MaxStreamLineCount = 100_000;
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex ComPortRegex = new(@"\b(COM\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<string> GetAvailablePorts()
    {
        string[] rawPorts = SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ExtractPortNumber)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
        if (string.IsNullOrWhiteSpace(cleanPort))
        {
            throw new ArgumentException($"Invalid COM port name: '{portName}'.");
        }

        int effectiveBaud = baudRate > 0 ? baudRate : 9600;

        // For bulk WLB streaming we need async BaseStream reads; open port off the UI thread first.
        if (string.Equals(commandMessage, "WLB", StringComparison.OrdinalIgnoreCase))
        {
            using var serialPort = await Task.Run(() =>
            {
                var sp = new SerialPort(cleanPort, effectiveBaud)
                {
                    WriteTimeout = 3000,
                    ReadTimeout = 5000
                };
                sp.Open();
                if (sp.BytesToRead > 0) sp.DiscardInBuffer();
                sp.WriteLine(commandMessage);
                return sp;
            }, cancellationToken);

            using var reg = cancellationToken.Register(() => { try { serialPort.Close(); } catch { } });
            await Task.Delay(200, cancellationToken);
            return await ReadBulkStreamAsync(serialPort, onStreamProgress, cancellationToken);
        }

        // Single-line handshake ("TEST") or configuration command: run entirely off UI thread
        return await Task.Run(() =>
        {
            using var serialPort = new SerialPort(cleanPort, effectiveBaud)
            {
                WriteTimeout = 3000,
                ReadTimeout = 5000
            };

            using var reg = cancellationToken.Register(() => { try { serialPort.Close(); } catch { } });
            serialPort.Open();

            if (serialPort.BytesToRead > 0)
            {
                serialPort.DiscardInBuffer();
            }

            if (string.Equals(commandMessage, "TEST", StringComparison.OrdinalIgnoreCase))
            {
                serialPort.WriteLine("WLB");
                Thread.Sleep(200);
                return serialPort.ReadLine();
            }

            serialPort.WriteLine(commandMessage);
            return serialPort.ReadLine();
        }, cancellationToken);
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

        var match = ComPortRegex.Match(portDisplay);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : portDisplay.Trim();
    }

    private static int ExtractPortNumber(string port)
    {
        var match = ComPortRegex.Match(port);
        if (match.Success && int.TryParse(match.Groups[1].Value[3..], out int num))
        {
            return num;
        }

        return int.MaxValue;
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
