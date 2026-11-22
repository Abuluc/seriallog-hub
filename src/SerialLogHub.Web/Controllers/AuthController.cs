using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using SerialLogHub.Web.Models;
using SerialLogHub.Web.Services;

namespace SerialLogHub.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private static readonly HashSet<string> DisposableEmailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "tempmail.com",
        "10minutemail.com",
        "mailinator.com",
        "tempmailto.org",
        "digopm.com",
        "smykwb.com"
    };

    private readonly ApiClientService _apiClient;
    private readonly ILogger<AuthController> _logger;

    public AuthController(ApiClientService apiClient, ILogger<AuthController> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] UserModel model, CancellationToken ct)
    {
        if (model == null ||
            string.IsNullOrWhiteSpace(model.Email) ||
            string.IsNullOrWhiteSpace(model.HashedPassword) ||
            string.IsNullOrWhiteSpace(model.Username))
        {
            return BadRequest(new { Error = "Email, username, and password are required." });
        }

        if (model.HashedPassword.Length < 6)
        {
            return BadRequest(new { Error = "Password must be at least 6 characters long." });
        }

        if (IsDisposableEmail(model.Email))
        {
            return BadRequest(new { Error = "Disposable email addresses are not permitted." });
        }

        try
        {
            var (success, result) = await _apiClient.RegisterAsync(model, ct);
            if (!success)
            {
                return BadRequest(new { Error = string.IsNullOrWhiteSpace(result) ? "Registration failed." : result });
            }

            return Ok(new { Message = "Registration successful." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed for {Email}.", model.Email);
            return StatusCode(502, new { Error = "Unable to reach authentication service." });
        }
    }

    [HttpPost("ResendVerif")]
    public async Task<IActionResult> ResendVerif([FromBody] ResendVerificationModel request, CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Email))
        {
            return BadRequest(new { Error = "Email address is required." });
        }

        try
        {
            var (success, result) = await _apiClient.ResendVerificationEmailAsync(request.Email, ct);
            if (!success)
            {
                return BadRequest(new { Error = result });
            }

            return Ok(new { Message = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resend verification failed.");
            return StatusCode(502, new { Error = "Unable to reach authentication service." });
        }
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] PasswordResetModel model, CancellationToken ct)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.Email))
        {
            return BadRequest(new { Error = "Invalid email address." });
        }

        try
        {
            var (success, result) = await _apiClient.SendResetMailAsync(model.Email, ct);
            if (!success)
            {
                return BadRequest(new { Error = result });
            }

            return Ok(new { success = true, Message = "Password reset link has been sent." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Password reset failed.");
            return StatusCode(502, new { Error = "Unable to reach authentication service." });
        }
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            HttpContext.Session.Clear();
            Response.Cookies.Delete("Connection");
        }

        return Ok(new { message = "Logged out successfully." });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] UserModel model, CancellationToken ct)
    {
        string? identifier = !string.IsNullOrWhiteSpace(model?.Email) ? model.Email : model?.Username;
        if (model == null || string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(model.HashedPassword))
        {
            return BadRequest(new { Error = "Email or username and password are required." });
        }

        model.Email = identifier.Trim();

        try
        {
            var (success, result) = await _apiClient.LoginAsync(model, ct);
            if (!success)
            {
                return BadRequest(new { Error = string.IsNullOrWhiteSpace(result) ? "Invalid credentials." : result });
            }

            string normalizedEmail = identifier.Trim().ToLowerInvariant();
            string? operatorToken = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(result);
                if (doc.RootElement.TryGetProperty("email", out var emailProp) &&
                    !string.IsNullOrWhiteSpace(emailProp.GetString()))
                {
                    normalizedEmail = emailProp.GetString()!.Trim().ToLowerInvariant();
                }
                if (doc.RootElement.TryGetProperty("operatorToken", out var tokenProp) &&
                    !string.IsNullOrWhiteSpace(tokenProp.GetString()))
                {
                    operatorToken = tokenProp.GetString()!.Trim();
                }
            }
            catch
            {
                // Keep fallback normalizedEmail
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, normalizedEmail),
                new(ClaimTypes.Email, normalizedEmail)
            };

            if (!string.IsNullOrWhiteSpace(operatorToken))
            {
                claims.Add(new Claim("OperatorToken", operatorToken));
            }

            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var authProperties = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1)
            };

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                authProperties);

            return Ok(new { Message = "Login successful.", Email = normalizedEmail });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed for {Email}.", model.Email);
            return StatusCode(502, new { Error = "Unable to reach authentication service." });
        }
    }

    private static bool IsDisposableEmail(string email)
    {
        int atIndex = email.LastIndexOf('@');
        if (atIndex < 0 || atIndex == email.Length - 1)
        {
            return true;
        }

        string domain = email[(atIndex + 1)..].Trim();
        return DisposableEmailDomains.Contains(domain);
    }
}
