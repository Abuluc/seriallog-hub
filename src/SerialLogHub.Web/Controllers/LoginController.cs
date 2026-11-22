using Microsoft.AspNetCore.Mvc;

namespace SerialLogHub.Web.Controllers;

public class LoginController : Controller
{
    private readonly IConfiguration _configuration;

    public LoginController(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        ViewBag.DemoModeEnabled = _configuration.GetValue("DemoMode:Enabled", false);
        ViewBag.DemoEmail = _configuration["DemoMode:DemoEmail"] ?? "demo@serialloghub.local";
        ViewBag.DemoUsername = _configuration["DemoMode:DemoUsername"] ?? "demo_operator";
        return View();
    }
}
