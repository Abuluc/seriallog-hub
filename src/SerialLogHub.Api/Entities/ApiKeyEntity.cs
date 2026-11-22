namespace SerialLogHub.Api.Entities;

public static class ApiCredentialClientTypes
{
    public const string DesktopBridge = "DesktopBridge";
    public const string OperatorSession = "OperatorSession";
}

public class ApiKeyEntity
{
    public int Id { get; set; }

    /// <summary>
    /// Stores the SHA-256 hash of the credential ("sha256:&lt;hex&gt;") so plaintext secrets are not persisted in SQLite.
    /// Indexed uniquely via IX_ApiKeys_Key for O(1) database lookup without loading keys into memory.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Authoritative operator email owning this credential. Wildcard ("*") or empty values are rejected.
    /// </summary>
    public string OwnerEmail { get; set; } = string.Empty;

    /// <summary>
    /// Credential type: DesktopBridge or OperatorSession.
    /// </summary>
    public string ClientType { get; set; } = ApiCredentialClientTypes.OperatorSession;

    public bool IsRevoked { get; set; }

    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddYears(1);
}
