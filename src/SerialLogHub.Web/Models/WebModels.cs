using System.Text.Json.Serialization;

namespace SerialLogHub.Web.Models;

public class UserModel
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string HashedPassword { get; set; } = string.Empty;
    public bool IsEmailVerified { get; set; }
    public string VerificationToken { get; set; } = "none";
    public string ResetPasswordToken { get; set; } = "none";
    public DateTime TokenExpiryTime { get; set; } = DateTime.UtcNow;
}

public class PasswordResetModel
{
    public string Email { get; set; } = string.Empty;
}

public class ResendVerificationModel
{
    public string Email { get; set; } = string.Empty;
}

public class UpdateNameRequestModel
{
    public string Email { get; set; } = string.Empty;
    public string NewValue { get; set; } = string.Empty;
    public string CurrentPassword { get; set; } = string.Empty;
}

public class UserProfileDto
{
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool IsEmailVerified { get; set; }
}

public class SerialPortModel
{
    public string SelectedPort { get; set; } = string.Empty;
    public string MessageToSend { get; set; } = string.Empty;
    public List<string> AvailablePorts { get; set; } = new();
    public List<int> BaudRates { get; set; } = new() { 9600, 19200, 38400, 57600, 115200 };
    public int SelectedBaudRate { get; set; } = 9600;
    public int CustomBaudRate { get; set; }
    public bool DemoModeEnabled { get; set; }
}

public class CalibrationModel
{
    public string SelectedPort { get; set; } = string.Empty;
    public int SelectedBaudRate { get; set; } = 9600;
}

public class ThresholdModel
{
    public int MinHum { get; set; }
    public int MaxHum { get; set; }
    public int MinTemp { get; set; }
    public int MaxTemp { get; set; }
}

public class CommandModel
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("SelecetdPort")]
    public string? SelecetdPort { get; set; }

    public int BaudRate { get; set; } = 9600;
    public string? Message { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? Response { get; set; }

    [JsonPropertyName("CommandID")]
    public string? CommandID { get; set; }

    public bool IsProcessed { get; set; }
}

public class PortsApiResponse
{
    public List<string> Ports { get; set; } = new();
}

public class AppHealthPayload
{
    public string UserName { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class WaitResponsePayload
{
    public string UserName { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

public class ErrorViewModel
{
    public string? RequestId { get; set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
