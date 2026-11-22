namespace SerialLogHub.Desktop.Services;

public sealed class BridgeEngine
{
    private readonly BridgeApiClient _apiClient;
    private readonly RealSerialTransport _realTransport;
    private readonly SimulatedSerialTransport _simulatedTransport;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _portSyncLock = new(1, 1);
    private readonly HashSet<string> _completedCommandIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dispatchedNonIdempotentIds = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _lastPublishedPorts = new();
    private int _consecutiveEmptyPolls;
    private int _consecutiveErrorCount;
    private DateTime _nextPollEarliestUtc = DateTime.MinValue;

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

    /// <summary>
    /// Returns true if a serial command mutates or erases device state and must NEVER be automatically retried.
    /// </summary>
    public static bool IsNonIdempotentPhysicalCommand(string? commandMessage)
    {
        if (string.IsNullOrWhiteSpace(commandMessage)) return false;
        string cmd = commandMessage.Trim().ToUpperInvariant();
        return cmd.StartsWith("WLE1", StringComparison.Ordinal) || // Flash memory erase
               cmd.StartsWith("WLH1", StringComparison.Ordinal) || // RTC write
               cmd.StartsWith("WLS0", StringComparison.Ordinal) || // Delayed start write
               cmd.StartsWith("WLM", StringComparison.Ordinal) ||  // Alarm threshold write
               cmd.StartsWith("WLA", StringComparison.Ordinal) ||  // Interval write
               cmd.StartsWith("WLV1", StringComparison.Ordinal) || // Auto-start trigger
               cmd.StartsWith("WLG", StringComparison.Ordinal);    // Label write
    }

    public async Task SyncPortsAsync(string userEmail, bool forcePublish = false, CancellationToken ct = default)
    {
        if (!await _portSyncLock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            var physicalPorts = await Task.Run(() => _realTransport.GetAvailablePorts(), ct);
            var ports = new List<string>(physicalPorts);
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
                _consecutiveEmptyPolls = 0;
                _nextPollEarliestUtc = DateTime.MinValue;
                PortsUpdated?.Invoke(ports);
                LogReceived?.Invoke($"Synced {ports.Count} COM port(s): {(ports.Count == 0 ? "None" : string.Join(", ", ports))}");
            }
            else
            {
                LogReceived?.Invoke("Warning: Failed to publish COM port list to API.");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"Port sync error: {ex.Message}");
        }
        finally
        {
            _portSyncLock.Release();
        }
    }

    public async Task PollAndExecuteNextCommandAsync(string userEmail, CancellationToken ct = default)
    {
        if (DateTime.UtcNow < _nextPollEarliestUtc)
        {
            return;
        }

        if (!await _commandLock.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            var command = await _apiClient.GetPendingCommandAsync(userEmail, ct);
            _consecutiveErrorCount = 0;

            if (command == null)
            {
                _consecutiveEmptyPolls++;
                // Adaptive idle backoff: fast polling when active, gentle backoff when idle
                if (_consecutiveEmptyPolls > 40)
                {
                    _nextPollEarliestUtc = DateTime.UtcNow.AddMilliseconds(2500);
                }
                else if (_consecutiveEmptyPolls > 15)
                {
                    _nextPollEarliestUtc = DateTime.UtcNow.AddMilliseconds(1200);
                }
                return;
            }

            // Reset adaptive backoff immediately when a command arrives
            _consecutiveEmptyPolls = 0;
            _nextPollEarliestUtc = DateTime.MinValue;

            if (!string.IsNullOrWhiteSpace(command.CommandId) && _completedCommandIds.Contains(command.CommandId))
            {
                await _apiClient.UpdateCommandResponseAsync(userEmail, command.CommandId, "Error: Command already processed locally.", ct);
                return;
            }

            if (!string.IsNullOrWhiteSpace(command.CommandId) &&
                IsNonIdempotentPhysicalCommand(command.Message) &&
                !_dispatchedNonIdempotentIds.Add(command.CommandId))
            {
                await _apiClient.UpdateCommandResponseAsync(
                    userEmail,
                    command.CommandId,
                    "Error: Non-idempotent hardware command was already dispatched once; duplicate execution blocked for device safety.",
                    ct);
                return;
            }

            string shortId = command.CommandId.Length > 8 ? command.CommandId[..8] : command.CommandId;
            LogReceived?.Invoke($"RX Command [{shortId}]: '{command.Message}' on {command.Port} @ {command.BaudRate}");

            string responseText;
            try
            {
                ISerialTransport transport = IncludeVirtualDemoDevice && _simulatedTransport.IsVirtualPort(command.Port)
                    ? _simulatedTransport
                    : _realTransport;

                using var cmdTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cmdTimeoutCts.CancelAfter(TimeSpan.FromSeconds(25));

                responseText = await transport.ExecuteCommandAsync(
                    command.Port,
                    command.BaudRate,
                    command.Message,
                    (count, finished, data) => _apiClient.SendStreamProgressAsync(userEmail, count, finished, data, ct),
                    cmdTimeoutCts.Token);

                string preview = responseText.Length > 80 ? responseText[..80] + "..." : responseText;
                LogReceived?.Invoke($"TX Response: {preview}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                responseText = "Error: Serial port operation timed out (no DataLogger response).";
                LogReceived?.Invoke($"Serial Timeout on {command.Port}.");
            }
            catch (Exception ex)
            {
                responseText = $"Error: {ex.Message}";
                LogReceived?.Invoke($"Serial Error on {command.Port}: {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(command.CommandId))
            {
                if (_completedCommandIds.Count > 500) _completedCommandIds.Clear();
                _completedCommandIds.Add(command.CommandId);
            }

            bool updated = await _apiClient.UpdateCommandResponseAsync(userEmail, command.CommandId, responseText, ct);
            if (!updated)
            {
                LogReceived?.Invoke($"Warning: Failed to report response for [{shortId}] back to API.");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown in progress
        }
        catch (Exception ex)
        {
            _consecutiveErrorCount++;
            int backoffSeconds = Math.Min(10, 1 << Math.Min(3, _consecutiveErrorCount));
            _nextPollEarliestUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);
            LogReceived?.Invoke($"Command poll error (retry in {backoffSeconds}s): {ex.Message}");
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
