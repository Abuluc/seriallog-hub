using SerialLogHub.Desktop.Models;

namespace SerialLogHub.Desktop.Services;

public sealed class BridgeEngine
{
    private readonly BridgeApiClient _apiClient;
    private readonly RealSerialTransport _realTransport;
    private readonly SimulatedSerialTransport _simulatedTransport;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private List<string> _lastPublishedPorts = new();

    public BridgeEngine(
        BridgeApiClient apiClient,
        RealSerialTransport realTransport,
        SimulatedSerialTransport simulatedTransport)
    {
        _apiClient = apiClient;
        _realTransport = realTransport;
        _simulatedTransport = simulatedTransport;
    }

    public bool IncludeVirtualDemoDevice { get; set; }

    public event Action<string>? LogReceived;
    public event Action<IReadOnlyList<string>>? PortsUpdated;

    public async Task SyncPortsAsync(string userEmail, bool forcePublish = false, CancellationToken ct = default)
    {
        var ports = new List<string>(_realTransport.GetAvailablePorts());
        if (IncludeVirtualDemoDevice)
        {
            ports.Add(_simulatedTransport.VirtualPortName);
        }

        bool changed = forcePublish || !ports.SequenceEqual(_lastPublishedPorts, StringComparer.OrdinalIgnoreCase);
        if (!changed)
        {
            return;
        }

        bool ok = await _apiClient.UpdatePortsAsync(userEmail, ports, ct);
        if (ok)
        {
            _lastPublishedPorts = ports;
            PortsUpdated?.Invoke(ports);
            LogReceived?.Invoke($"Synced {ports.Count} COM port(s): {(ports.Count == 0 ? "None" : string.Join(", ", ports))}");
        }
    }

    public async Task PollAndExecuteNextCommandAsync(string userEmail, CancellationToken ct = default)
    {
        if (!await _commandLock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            var command = await _apiClient.GetPendingCommandAsync(userEmail, ct);
            if (command == null)
            {
                return;
            }

            LogReceived?.Invoke($"RX Command [{command.CommandId[..Math.Min(8, command.CommandId.Length)]}]: '{command.Message}' on {command.Port} @ {command.BaudRate}");

            string responseText;
            try
            {
                ISerialTransport transport = IncludeVirtualDemoDevice && _simulatedTransport.IsVirtualPort(command.Port)
                    ? _simulatedTransport
                    : _realTransport;

                responseText = await transport.ExecuteCommandAsync(
                    command.Port,
                    command.BaudRate,
                    command.Message,
                    (count, finished, data) => _apiClient.SendStreamProgressAsync(userEmail, count, finished, data, ct),
                    ct);

                string preview = responseText.Length > 80 ? responseText[..80] + "..." : responseText;
                LogReceived?.Invoke($"TX Response: {preview}");
            }
            catch (Exception ex)
            {
                responseText = $"Error: {ex.Message}";
                LogReceived?.Invoke($"Serial Error on {command.Port}: {ex.Message}");
            }

            await _apiClient.UpdateCommandResponseAsync(userEmail, command.CommandId, responseText, ct);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async Task SendHeartbeatAsync(string userEmail, CancellationToken ct = default)
    {
        try
        {
            await _apiClient.SendWebHeartbeatAsync(userEmail, ct);
        }
        catch
        {
            // Ignore transient offline Web App heartbeat errors
        }
    }
}
