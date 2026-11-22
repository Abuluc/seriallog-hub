using System.Security.Cryptography;
using System.Text;

namespace SerialLogHub.Api.Services;

public static class OperatorScopeGuard
{
    public const string OperatorEmailHeaderName = "X-OPERATOR-EMAIL";

    /// <summary>
    /// Constant-time comparison for API keys, Bridge keys, and password reset tokens.
    /// Prevents timing side-channel leaks.
    /// </summary>
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

    /// <summary>
    /// Enforces BOLA / IDOR protection: when a caller supplies an authenticated operator scope
    /// in X-OPERATOR-EMAIL (sent by SerialLogHub.Web or SerialLogHub.Desktop), it must match
    /// the requested target resource email.
    /// </summary>
    public static bool IsAuthorizedForOperator(HttpContext? httpContext, string? targetEmail)
    {
        if (string.IsNullOrWhiteSpace(targetEmail))
        {
            return false;
        }

        if (httpContext == null)
        {
            return true;
        }

        if (!httpContext.Request.Headers.TryGetValue(OperatorEmailHeaderName, out var scopeHeader) ||
            string.IsNullOrWhiteSpace(scopeHeader))
        {
            return true;
        }

        string scopedEmail = scopeHeader.ToString().Trim();
        string requestedEmail = targetEmail.Trim();
        return string.Equals(scopedEmail, requestedEmail, StringComparison.OrdinalIgnoreCase);
    }
}
