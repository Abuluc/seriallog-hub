using System.Security.Cryptography;
using System.Text;

namespace SerialLogHub.Web.Services;

public static class HashHelper
{
    public static string ComputeSha256(string input)
    {
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
