using System.Security.Cryptography;
using System.Text;
using SerialLogHub.Api.Services;
using Xunit;

namespace SerialLogHub.Tests;

public class PasswordSecurityServiceTests
{
    private readonly PasswordSecurityService _service = new();

    [Fact]
    public void HashAndVerify_WorksAcrossRawRegisterAndSha256Login()
    {
        const string email = "operator@serialloghub.local";
        const string rawPassword = "StrongPassword123!";

        string stored = _service.HashForStorage(email, rawPassword);
        string clientSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPassword))).ToLowerInvariant();

        Assert.True(_service.VerifyPassword(email, stored, clientSha256));
        Assert.True(_service.VerifyPassword(email, stored, rawPassword));
        Assert.False(_service.VerifyPassword(email, stored, "WrongPassword!"));
    }

    [Fact]
    public void HashForStorage_ProducesUniqueSaltedHashes()
    {
        const string email = "operator@serialloghub.local";
        const string password = "SamePassword123!";

        string hash1 = _service.HashForStorage(email, password);
        string hash2 = _service.HashForStorage(email, password);

        Assert.NotEqual(hash1, hash2);
    }
}
