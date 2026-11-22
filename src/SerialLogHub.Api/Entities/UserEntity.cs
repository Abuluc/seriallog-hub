namespace SerialLogHub.Api.Entities;

public class UserEntity
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string HashedPassword { get; set; } = string.Empty;
    public bool IsEmailVerified { get; set; }
    public string VerificationToken { get; set; } = string.Empty;
    public string ResetPasswordToken { get; set; } = string.Empty;
    public DateTime TokenExpiryTime { get; set; } = DateTime.UtcNow;
}
