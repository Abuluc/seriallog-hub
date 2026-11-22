namespace SerialLogHub.Desktop.Services;

/// <summary>
/// Abstraction over serial port communication so physical hardware (RealSerialTransport)
/// and the optional Virtual DataLogger Simulator (SimulatedSerialTransport) remain
/// completely isolated.
/// </summary>
public interface ISerialTransport
{
    IReadOnlyList<string> GetAvailablePorts();

    Task<string> ExecuteCommandAsync(
        string portName,
        int baudRate,
        string commandMessage,
        Func<int, bool, string, Task>? onStreamProgress = null,
        CancellationToken cancellationToken = default);
}
