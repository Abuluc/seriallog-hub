using Microsoft.AspNetCore.Mvc;

namespace SerialLogHub.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public HealthController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            service = "SerialLogHub.Api",
            status = "Healthy",
            demoMode = _configuration.GetValue("DemoMode:Enabled", false),
            timestampUtc = DateTime.UtcNow
        });
    }
}
