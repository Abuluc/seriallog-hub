namespace SerialLogHub.Api.Entities;

public static class ApiCredentialClientTypes
{
    public const string WebServer = "WebServer";
    public const string DesktopBridge = "DesktopBridge";
    public const string OperatorSession = "OperatorSession";
}

public class ApiKeyEntity
{
    public int Id { get; set; }

    /// <summary>
    /// Stores the SHA-256 hash of the credential ("sha256:&lt;hex&gt;") so plaintext secrets are not persisted in SQLite.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Bound operator email for scoped Desktop Bridge / Operator Session tokens, or "*" for trusted WebServer proxy credentials.
    /// </summary>
    public string OwnerEmail { get; set; } = "*";

    /// <summary>
    /// Distinguishes WebServer credentials from DesktopBridge / OperatorSession credentials.
    /// </summary>
    public string ClientType { get; set; } = ApiCredentialClientTypes.WebServer;

    public bool IsRevoked { get; set; }

    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddYears(1);
}
