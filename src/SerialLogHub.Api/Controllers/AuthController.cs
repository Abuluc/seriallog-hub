using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SerialLogHub.Api.Data;
using SerialLogHub.Api.Entities;
using SerialLogHub.Api.Models;
using SerialLogHub.Api.Services;

namespace SerialLogHub.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PasswordSecurityService _passwordService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        AppDbContext db,
        PasswordSecurityService passwordService,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _db = db;
        _passwordService = passwordService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] UserAuthRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.HashedPassword))
        {
            return BadRequest("Invalid login credentials.");
        }

        string normalizedEmail = request.Email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);

        if (user == null)
        {
            return NotFound("User not found.");
        }

        if (!string.IsNullOrWhiteSpace(request.Username) &&
            !string.Equals(user.Username, request.Username.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Invalid username.");
        }

        if (!_passwordService.VerifyPassword(user.Email, user.HashedPassword, request.HashedPassword))
        {
            return Unauthorized("Incorrect password.");
        }

        if (!user.IsEmailVerified)
        {
            return BadRequest("Email address is not verified.");
        }

        return Ok(new
        {
            message = "Login successful.",
            username = user.Username,
            email = user.Email
        });
    }

    [HttpPost("{culture}/register")]
    public async Task<IActionResult> Register(string culture, [FromBody] UserAuthRequest request)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.HashedPassword))
        {
            return BadRequest("Email, username, and password are required.");
        }

        string normalizedEmail = request.Email.Trim().ToLowerInvariant();
        bool exists = await _db.Users.AnyAsync(u => u.Email.ToLower() == normalizedEmail);
        if (exists)
        {
            return BadRequest("This email address is already registered.");
        }

        bool demoMode = _configuration.GetValue("DemoMode:Enabled", false);
        string verificationToken = Guid.NewGuid().ToString("N");

        var newUser = new UserEntity
        {
            Username = request.Username.Trim(),
            Email = normalizedEmail,
            HashedPassword = _passwordService.HashForStorage(normalizedEmail, request.HashedPassword),
            IsEmailVerified = demoMode,
            VerificationToken = verificationToken,
            ResetPasswordToken = "None",
            TokenExpiryTime = DateTime.UtcNow.AddHours(24)
        };

        _db.Users.Add(newUser);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Registered user {Email} (autoVerified={AutoVerified}).", normalizedEmail, demoMode);

        return Ok(new
        {
            message = demoMode
                ? "Registration successful (auto-verified in Demo Mode)."
                : "Registration successful. Please verify your email address.",
            email = newUser.Email,
            username = newUser.Username
        });
    }

    [HttpPost("updatename")]
    public async Task<IActionResult> UpdateName([FromBody] UpdateNameRequest request)
    {
        if (request == null ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.NewValue) ||
            string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            return BadRequest("Email, new username, and current password are required.");
        }

        string normalizedEmail = request.Email.Trim().ToLowerInvariant();
        if (_configuration.GetValue("DemoMode:Enabled", false))
        {
            string demoEmail = (_configuration["DemoMode:DemoEmail"] ?? "demo@serialloghub.local").Trim().ToLowerInvariant();
            if (string.Equals(normalizedEmail, demoEmail, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest("The shared demo operator account cannot be modified.");
            }
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        if (!_passwordService.VerifyPassword(user.Email, user.HashedPassword, request.CurrentPassword))
        {
            return Unauthorized("Incorrect current password.");
        }

        user.Username = request.NewValue.Trim();
        await _db.SaveChangesAsync();

        return Ok(new { message = "Username updated successfully.", username = user.Username });
    }

    [HttpPost("{culture}/SendResetMail")]
    public async Task<IActionResult> SendResetMail(string culture, [FromBody] JsonElement body)
    {
        string? email = ExtractEmailFromBody(body);
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Invalid email address.");
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);
        if (user != null)
        {
            user.ResetPasswordToken = Guid.NewGuid().ToString("N");
            user.TokenExpiryTime = DateTime.UtcNow.AddHours(1);
            await _db.SaveChangesAsync();
            _logger.LogInformation("Generated password reset token for {Email}.", normalizedEmail);
        }

        return Ok("Password reset link has been sent.");
    }

    [HttpPost("{culture}/resendverificationemail")]
    public async Task<IActionResult> ResendVerificationEmail(string culture, [FromBody] JsonElement body)
    {
        string? email = ExtractEmailFromBody(body);
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Invalid email address.");
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        if (user.IsEmailVerified)
        {
            return BadRequest("Email address is already verified.");
        }

        user.VerificationToken = Guid.NewGuid().ToString("N");
        user.TokenExpiryTime = DateTime.UtcNow.AddHours(24);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Regenerated verification token for {Email}.", normalizedEmail);
        return Ok("Verification email has been resent.");
    }

    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile([FromQuery] string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest(new { message = "Email parameter is required." });
        }

        string normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);
        if (user == null)
        {
            return NotFound(new { message = "User not found." });
        }

        return Ok(new UserProfileResponse
        {
            Email = user.Email,
            Username = user.Username,
            IsEmailVerified = user.IsEmailVerified
        });
    }

    private static string? ExtractEmailFromBody(JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.String)
        {
            return body.GetString();
        }

        if (body.ValueKind == JsonValueKind.Object)
        {
            if (body.TryGetProperty("email", out var lower) && lower.ValueKind == JsonValueKind.String)
            {
                return lower.GetString();
            }

            if (body.TryGetProperty("Email", out var upper) && upper.ValueKind == JsonValueKind.String)
            {
                return upper.GetString();
            }
        }

        return null;
    }
}
