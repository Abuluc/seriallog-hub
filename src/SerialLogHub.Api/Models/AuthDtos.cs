namespace SerialLogHub.Api.Models;

public class UserAuthRequest
{
    public int Id { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? HashedPassword { get; set; }
    public bool IsEmailVerified { get; set; }
    public string? VerificationToken { get; set; }
    public string? ResetPasswordToken { get; set; }
    public DateTime TokenExpiryTime { get; set; }
}

public class UpdateNameRequest
{
    public string? Email { get; set; }
    public string? NewValue { get; set; }
    public string? CurrentPassword { get; set; }
}

public class ConfirmPasswordResetRequest
{
    public string? Email { get; set; }
    public string? ResetToken { get; set; }
    public string? NewPassword { get; set; }
}

public class UserProfileResponse
{
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool IsEmailVerified { get; set; }
}
