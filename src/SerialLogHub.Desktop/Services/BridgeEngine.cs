using System.Text.Json;

namespace SerialLogHub.Desktop.Services;

public sealed class BufferedCommandResponse
{
    public string UserEmail { get; set; } = string.Empty;
    public string CommandId { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public DateTime QueuedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class BridgeEngine
{
    public const int MaxBufferedResponses = 50;

    private readonly BridgeApiClient _apiClient;
    private readonly RealSerialTransport _realTransport;
    private readonly SimulatedSerialTransport _simulatedTransport;
    private readonly string _durableBufferFilePath;
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _portSyncLock = new(1, 1);
    private readonly HashSet<string> _completedCommandIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dispatchedNonIdempotentIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BufferedCommandResponse> _pendingResponses = new();
    private List<string> _lastPublishedPorts = new();
    private int _consecutiveEmptyPolls;
    private int _consecutiveErrorCount;
    private DateTime _nextPollEarliestUtc = DateTime.MinValue;

    public BridgeEngine(
        BridgeApiClient apiClient,
        RealSerialTransport realTransport,
        SimulatedSerialTransport simulatedTransport,
        string? durableBufferFilePath = null)
    {
        _apiClient = apiClient;
        _realTransport = realTransport;
        _simulatedTransport = simulatedTransport;
        _durableBufferFilePath = durableBufferFilePath ??
            Path.Combine(Path.GetTempPath(), "serialloghub_bridge_pending_responses.json");

        LoadBufferedResponsesFromDisk();
    }

    public bool IncludeVirtualDemoDevice { get; set; }
    public int BufferedResponseCount => _pendingResponses.Count;

    public event Action<string>? LogReceived;
    public event Action<IReadOnlyList<string>>? PortsUpdated;

    public static bool IsNonIdempotentPhysicalCommand(string? commandMessage)
    {
        if (string.IsNullOrWhiteSpace(commandMessage)) return false;
        string cmd = commandMessage.Trim().ToUpperInvariant();
        return cmd.StartsWith("WLE1", StringComparison.Ordinal) ||
               cmd.StartsWith("WLH1", StringComparison.Ordinal) ||
               cmd.StartsWith("WLS0", StringComparison.Ordinal) ||
               cmd.StartsWith("WLM", StringComparison.Ordinal) ||
               cmd.StartsWith("WLA", StringComparison.Ordinal) ||
               cmd.StartsWith("WLV1", StringComparison.Ordinal) ||
               cmd.StartsWith("WLG", StringComparison.Ordinal);
    }

    public void EnqueueBufferedResponse(string userEmail, string commandId, string responseText)
    {
        if (string.IsNullOrWhiteSpace(commandId)) return;

        // Deduplicate by CommandId
        if (_pendingResponses.Any(r => string.Equals(r.CommandId, commandId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Enforce strict bounded queue limit (evict oldest when full)
        while (_pendingResponses.Count >= MaxBufferedResponses)
        {
            _pendingResponses.RemoveAt(0);
        }

        _pendingResponses.Add(new BufferedCommandResponse
        {
            UserEmail = userEmail,
            CommandId = commandId,
            ResponseText = responseText,
            QueuedAtUtc = DateTime.UtcNow
        });

        SaveBufferedResponsesToDisk();
    }

    private void LoadBufferedResponsesFromDisk()
    {
        try
        {
            if (!File.Exists(_durableBufferFilePath)) return;
            string json = File.ReadAllText(_durableBufferFilePath);
            var items = JsonSerializer.Deserialize<List<BufferedCommandResponse>>(json);
            if (items != null)
            {
                foreach (var item in items.TakeLast(MaxBufferedResponses))
                {
                    if (!string.IsNullOrWhiteSpace(item.CommandId))
                    {
                        _pendingResponses.Add(item);
                        _completedCommandIds.Add(item.CommandId);
                    }
                }
            }
        }
        catch
        {
            // Ignore corrupted local buffer file on startup
        }
    }

    private void SaveBufferedResponsesToDisk()
    {
        try
        {
            if (_pendingResponses.Count == 0)
            {
                if (File.Exists(_durableBufferFilePath)) File.Delete(_durableBufferFilePath);
                return;
            }
            string json = JsonSerializer.Serialize(_pendingResponses);
            File.WriteAllText(_durableBufferFilePath, json);
        }
        catch
        {
            // Non-fatal if temp directory is read-only
        }
    }

    private async Task FlushBufferedResponsesAsync(CancellationToken ct)
    {
        if (_pendingResponses.Count == 0) return;

        var snapshot = _pendingResponses.ToList();
        bool changed = false;

        foreach (var item in snapshot)
        {
            var (success, isPermanent) = await _apiClient.UpdateCommandResponseDetailedAsync(
                item.UserEmail, item.CommandId, item.ResponseText, ct);

            if (success || isPermanent)
            {
                _pendingResponses.Remove(item);
                changed = true;
                if (success)
                {
                    LogReceived?.Invoke($"Flushed buffered response for command [{item.CommandId}].");
                }
            }
            else
            {
                // Network still unavailable; stop flushing until next cycle
                break;
            }
        }

        if (changed)
        {
            SaveBufferedResponsesToDisk();
        }
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
            await FlushBufferedResponsesAsync(ct);

            var command = await _apiClient.GetPendingCommandAsync(userEmail, ct);
            _consecutiveErrorCount = 0;

            if (command == null)
            {
                _consecutiveEmptyPolls++;
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

            _consecutiveEmptyPolls = 0;
            _nextPollEarliestUtc = DateTime.MinValue;

            if (!string.IsNullOrWhiteSpace(command.CommandId) && _completedCommandIds.Contains(command.CommandId))
            {
                await _apiClient.UpdateCommandResponseDetailedAsync(userEmail, command.CommandId, "Error: Command already processed locally.", ct);
                return;
            }

            if (!string.IsNullOrWhiteSpace(command.CommandId) &&
                IsNonIdempotentPhysicalCommand(command.Message) &&
                !_dispatchedNonIdempotentIds.Add(command.CommandId))
            {
                await _apiClient.UpdateCommandResponseDetailedAsync(
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

            var (updated, isPermanent) = await _apiClient.UpdateCommandResponseDetailedAsync(userEmail, command.CommandId, responseText, ct);
            if (!updated && !isPermanent && !string.IsNullOrWhiteSpace(command.CommandId))
            {
                EnqueueBufferedResponse(userEmail, command.CommandId, responseText);
                LogReceived?.Invoke($"Buffered response for [{shortId}] locally ({_pendingResponses.Count}/{MaxBufferedResponses} queued) for delivery upon reconnect.");
            }
            else if (!updated && isPermanent)
            {
                LogReceived?.Invoke($"Permanent API rejection for [{shortId}]; response was not queued for retry.");
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
