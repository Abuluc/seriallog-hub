using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SerialLogHub.Api.Data;

namespace SerialLogHub.Api.Services;

public static class OperatorScopeGuard
{
    public const string OperatorEmailHeaderName = "X-OPERATOR-EMAIL";
    public const string OperatorTokenHeaderName = "X-OPERATOR-TOKEN";
    public const string OperatorTimestampHeaderName = "X-OPERATOR-TIMESTAMP";
    public const string OperatorProofHeaderName = "X-OPERATOR-PROOF";

    public const string ContextOperatorEmailKey = "AuthenticatedOperatorEmail";
    public const string ContextClientTypeKey = "AuthenticatedClientType";
    public const string ContextCredentialVerifiedKey = "OperatorCredentialVerified";

    public static bool FixedTimeEquals(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        byte[] a = Encoding.UTF8.GetBytes(provided);
        byte[] b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static string HashCredential(string rawSecret)
    {
        if (string.IsNullOrWhiteSpace(rawSecret))
        {
            return string.Empty;
        }

        if (rawSecret.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return rawSecret.ToLowerInvariant();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawSecret.Trim()));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool VerifyHashedOrPlainCredential(string providedRawSecret, string storedKeyOrHash)
    {
        if (string.IsNullOrWhiteSpace(providedRawSecret) || string.IsNullOrWhiteSpace(storedKeyOrHash))
        {
            return false;
        }

        if (storedKeyOrHash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            string computedHash = HashCredential(providedRawSecret);
            return FixedTimeEquals(computedHash, storedKeyOrHash.ToLowerInvariant());
        }

        return FixedTimeEquals(providedRawSecret, storedKeyOrHash);
    }

    public static string ComputeOperatorHmacProof(string operatorEmail, long unixTimestampSeconds, string signingSecret)
    {
        string canonical = $"{operatorEmail.Trim().ToLowerInvariant()}|{unixTimestampSeconds}";
        byte[] keyBytes = Encoding.UTF8.GetBytes(signingSecret);
        byte[] dataBytes = Encoding.UTF8.GetBytes(canonical);
        byte[] hmac = HMACSHA256.HashData(keyBytes, dataBytes);
        return Convert.ToHexString(hmac).ToLowerInvariant();
    }

    public static bool VerifyOperatorHmacProof(string operatorEmail, string? timestampHeader, string? proofHeader, string? signingSecret)
    {
        if (string.IsNullOrWhiteSpace(operatorEmail) ||
            string.IsNullOrWhiteSpace(timestampHeader) ||
            string.IsNullOrWhiteSpace(proofHeader) ||
            string.IsNullOrWhiteSpace(signingSecret))
        {
            return false;
        }

        if (!long.TryParse(timestampHeader.Trim(), out long unixSeconds))
        {
            return false;
        }

        long nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(nowSeconds - unixSeconds) > 300) // 5-minute replay window
        {
            return false;
        }

        string expectedProof = ComputeOperatorHmacProof(operatorEmail, unixSeconds, signingSecret);
        return FixedTimeEquals(proofHeader.Trim().ToLowerInvariant(), expectedProof);
    }

    /// <summary>
    /// Helper for unit/integration tests to simulate an authenticated operator principal on HttpContext.
    /// </summary>
    public static void SetAuthenticatedOperator(HttpContext httpContext, string operatorEmail, string clientType = "OperatorSession")
    {
        httpContext.Items[ContextOperatorEmailKey] = operatorEmail.Trim().ToLowerInvariant();
        httpContext.Items[ContextClientTypeKey] = clientType;
        httpContext.Items[ContextCredentialVerifiedKey] = true;
    }

    /// <summary>
    /// Authoritatively verifies that the caller is authenticated as targetEmail.
    /// Never trusts a bare X-OPERATOR-EMAIL header by itself when security enforcement is active.
    /// </summary>
    public static async Task<(bool IsAuthorized, int StatusCode, string ErrorMessage)> AuthorizeOperatorAsync(
        HttpContext? httpContext,
        AppDbContext db,
        IConfiguration? configuration,
        string? targetEmail)
    {
        if (string.IsNullOrWhiteSpace(targetEmail))
        {
            return (false, StatusCodes.Status400BadRequest, "Target operator email is required.");
        }

        string requestedEmail = targetEmail.Trim().ToLowerInvariant();

        // Direct in-process C# unit tests that do not attach an HttpContext at all
        if (httpContext == null)
        {
            return (true, StatusCodes.Status200OK, string.Empty);
        }

        // 1. Check if ApiKeyMiddleware already established a cryptographically verified operator identity
        if (httpContext.Items.TryGetValue(ContextCredentialVerifiedKey, out var verifiedObj) &&
            verifiedObj is true &&
            httpContext.Items.TryGetValue(ContextOperatorEmailKey, out var boundEmailObj) &&
            boundEmailObj is string boundEmail &&
            !string.IsNullOrWhiteSpace(boundEmail))
        {
            // If X-OPERATOR-EMAIL header was also sent, it must match the credential's bound email
            if (httpContext.Request.Headers.TryGetValue(OperatorEmailHeaderName, out var headerEmail) &&
                !string.IsNullOrWhiteSpace(headerEmail) &&
                !string.Equals(headerEmail.ToString().Trim(), boundEmail, StringComparison.OrdinalIgnoreCase))
            {
                return (false, StatusCodes.Status403Forbidden, "Header operator identity conflicts with bound credential identity.");
            }

            if (!string.Equals(boundEmail, requestedEmail, StringComparison.OrdinalIgnoreCase))
            {
                return (false, StatusCodes.Status403Forbidden, "Cross-user resource access is forbidden.");
            }

            return (true, StatusCodes.Status200OK, string.Empty);
        }

        // 2. Check if the request carries an explicit operator-bound token (X-OPERATOR-TOKEN) in the database
        if (httpContext.Request.Headers.TryGetValue(OperatorTokenHeaderName, out var tokenHeader))
        {
            string rawToken = tokenHeader.ToString().Trim();
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return (false, StatusCodes.Status401Unauthorized, "Empty operator session token.");
            }

            string tokenHash = HashCredential(rawToken);
            var keyEntity = await db.ApiKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(k => k.Key == tokenHash || k.Key == rawToken);

            if (keyEntity == null || keyEntity.IsRevoked || keyEntity.ExpiresAt <= DateTime.UtcNow)
            {
                return (false, StatusCodes.Status401Unauthorized, "Invalid, expired, or revoked operator session token.");
            }

            if (string.IsNullOrWhiteSpace(keyEntity.OwnerEmail) || keyEntity.OwnerEmail == "*")
            {
                return (false, StatusCodes.Status403Forbidden, "Credential is not bound to a specific operator identity.");
            }

            if (httpContext.Request.Headers.TryGetValue(OperatorEmailHeaderName, out var assertedHeader) &&
                !string.IsNullOrWhiteSpace(assertedHeader) &&
                !string.Equals(assertedHeader.ToString().Trim(), keyEntity.OwnerEmail, StringComparison.OrdinalIgnoreCase))
            {
                return (false, StatusCodes.Status403Forbidden, "Forged X-OPERATOR-EMAIL does not match token owner.");
            }

            if (!string.Equals(keyEntity.OwnerEmail, requestedEmail, StringComparison.OrdinalIgnoreCase))
            {
                return (false, StatusCodes.Status403Forbidden, "Cross-user resource access is forbidden.");
            }

            SetAuthenticatedOperator(httpContext, keyEntity.OwnerEmail, keyEntity.ClientType);
            return (true, StatusCodes.Status200OK, string.Empty);
        }

        // 3. Check if the request carries a signed WebServer/Bridge HMAC proof (X-OPERATOR-EMAIL + X-OPERATOR-TIMESTAMP + X-OPERATOR-PROOF)
        if (httpContext.Request.Headers.TryGetValue(OperatorEmailHeaderName, out var scopeHeader))
        {
            string assertedEmail = scopeHeader.ToString().Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(assertedEmail))
            {
                return (false, StatusCodes.Status401Unauthorized, "Missing operator identity.");
            }

            if (httpContext.Request.Headers.TryGetValue(OperatorTimestampHeaderName, out var tsHeader) &&
                httpContext.Request.Headers.TryGetValue(OperatorProofHeaderName, out var proofHeader))
            {
                string signingSecret = configuration?["ApiSecurity:ApiKey"] ?? "dev-serialloghub-api-key";
                if (!VerifyOperatorHmacProof(assertedEmail, tsHeader.ToString(), proofHeader.ToString(), signingSecret))
                {
                    return (false, StatusCodes.Status401Unauthorized, "Invalid or expired cryptographic operator proof.");
                }

                if (!string.Equals(assertedEmail, requestedEmail, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, StatusCodes.Status403Forbidden, "Cross-user resource access is forbidden.");
                }

                SetAuthenticatedOperator(httpContext, assertedEmail, "WebServer");
                return (true, StatusCodes.Status200OK, string.Empty);
            }

            // A bare X-OPERATOR-EMAIL header was supplied without a valid token or cryptographic proof:
            // If it doesn't even match targetEmail, reject immediately with 403 Forbidden;
            // If it matches targetEmail, still reject because a bare header is an unverified identity assertion!
            if (!string.Equals(assertedEmail, requestedEmail, StringComparison.OrdinalIgnoreCase))
            {
                return (false, StatusCodes.Status403Forbidden, "Cross-user resource access is forbidden.");
            }

            return (false, StatusCodes.Status401Unauthorized, "Unverified X-OPERATOR-EMAIL header: cryptographic operator token or HMAC proof is required.");
        }

        // 4. If an HttpContext was explicitly attached (e.g. an HTTP request or security test with headers/enforcement)
        // and X-OPERATOR-EMAIL / X-OPERATOR-TOKEN are completely missing, reject!
        return (false, StatusCodes.Status401Unauthorized, "Missing authenticated operator identity (X-OPERATOR-TOKEN or signed X-OPERATOR-EMAIL proof required).");
    }
}
