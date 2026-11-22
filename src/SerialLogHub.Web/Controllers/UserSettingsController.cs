using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SerialLogHub.Web.Models;
using SerialLogHub.Web.Services;

namespace SerialLogHub.Web.Controllers;

[Authorize]
public class UserSettingsController : Controller
{
    private readonly ApiClientService _apiClient;

    public UserSettingsController(ApiClientService apiClient)
    {
        _apiClient = apiClient;
    }

    public IActionResult Index() => View();

    [HttpPost("GetUserName")]
    public async Task<IActionResult> GetUserName(CancellationToken ct)
    {
        string? email = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(email))
        {
            return Unauthorized(new { message = "User session not found." });
        }

        var profile = await _apiClient.GetProfileAsync(email, ct);
        if (profile == null)
        {
            return NotFound(new { message = "User not found." });
        }

        return Ok(new { username = profile.Username, email = profile.Email });
    }

    [HttpPost("updatename")]
    public async Task<IActionResult> UpdateName([FromBody] UpdateNameRequestModel model, CancellationToken ct)
    {
        string? email = User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(email))
        {
            return Unauthorized(new { message = "User session not found." });
        }

        if (model == null || string.IsNullOrWhiteSpace(model.NewValue) || string.IsNullOrWhiteSpace(model.CurrentPassword))
        {
            return BadRequest(new { message = "New username and current password are required." });
        }

        model.Email = email;
        var (success, result) = await _apiClient.UpdateNameAsync(model, ct);
        if (!success)
        {
            return BadRequest(new { message = result.Contains("şifre", StringComparison.OrdinalIgnoreCase) ? "Incorrect current password." : result });
        }

        return Ok(new { message = "Username updated successfully.", username = model.NewValue });
    }
}
