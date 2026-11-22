using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;

namespace SerialLogHub.Api.Services;

public class PasswordSecurityService
{
    private static readonly Regex Sha256HexRegex = new("^[a-fA-F0-9]{64}$", RegexOptions.Compiled);
    private readonly PasswordHasher<string> _passwordHasher = new();

    /// <summary>
    /// Normalizes incoming client password input: if the client sent a raw password
    /// (as in legacy Register), computes SHA-256 first so Login (which sends SHA-256)
    /// and Register share the same canonical pre-hash before salted PBKDF2 storage.
    /// </summary>
    public string NormalizeClientHash(string rawOrSha256)
    {
        if (string.IsNullOrEmpty(rawOrSha256))
        {
            return string.Empty;
        }

        if (Sha256HexRegex.IsMatch(rawOrSha256))
        {
            return rawOrSha256.ToLowerInvariant();
        }

        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawOrSha256));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public string HashForStorage(string email, string rawOrSha256)
    {
        string canonical = NormalizeClientHash(rawOrSha256);
        return _passwordHasher.HashPassword(email.ToLowerInvariant(), canonical);
    }

    public bool VerifyPassword(string email, string storedHash, string incomingRawOrSha256)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(incomingRawOrSha256))
        {
            return false;
        }

        string canonical = NormalizeClientHash(incomingRawOrSha256);
        var result = _passwordHasher.VerifyHashedPassword(email.ToLowerInvariant(), storedHash, canonical);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
