using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SerialLogHub.Web.Controllers;
using SerialLogHub.Web.Models;
using SerialLogHub.Web.Services;
using Xunit;

namespace SerialLogHub.Tests;

public sealed class WebLayerTests
{
    [Fact]
    public void UserConnectionManager_SupportsMultipleTabsPerUser()
    {
        var manager = new UserConnectionManager();
        manager.AddConnection("demo@serialloghub.local", "conn-tab-1");
        manager.AddConnection("demo@serialloghub.local", "conn-tab-2");

        var all = manager.GetConnectionIds("DEMO@SERIALLOGHUB.LOCAL");
        Assert.Equal(2, all.Count);
        Assert.Contains("conn-tab-1", all);
        Assert.Contains("conn-tab-2", all);

        manager.RemoveConnection("conn-tab-1");
        var remaining = manager.GetConnectionIds("demo@serialloghub.local");
        Assert.Single(remaining);
        Assert.Equal("conn-tab-2", remaining[0]);

        manager.RemoveConnection("conn-tab-2");
        Assert.Empty(manager.GetConnectionIds("demo@serialloghub.local"));
    }

    [Theory]
    [InlineData(40, 10, 20, 80)]
    [InlineData(20, 20, 20, 80)]
    [InlineData(10, 30, -5, 80)]
    [InlineData(10, 30, 20, 105)]
    [InlineData(10, 30, 60, 40)]
    public async Task SettingsController_SetMM_RejectsInvalidThresholds(int minTemp, int maxTemp, int minHum, int maxHum)
    {
        var config = new ConfigurationBuilder().Build();
        var apiClient = new ApiClientService(new HttpClient(), config, NullLogger<ApiClientService>.Instance);
        var controller = new SettingsController(apiClient);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.Name, "demo@serialloghub.local") }, "TestAuth"))
            }
        };

        var result = await controller.SetMM(
            new CalibrationModel { SelectedPort = "COM99", SelectedBaudRate = 9600 },
            new ThresholdModel { MinTemp = minTemp, MaxTemp = maxTemp, MinHum = minHum, MaxHum = maxHum },
            CancellationToken.None);

        string json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"status\":\"error\"", json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No COM ports detected")]
    [InlineData("No active session")]
    public async Task HomeController_TestConnection_RejectsPlaceholderPorts(string port)
    {
        var config = new ConfigurationBuilder().Build();
        var apiClient = new ApiClientService(new HttpClient(), config, NullLogger<ApiClientService>.Instance);
        var controller = new HomeController(
            NullLogger<HomeController>.Instance,
            apiClient,
            null!,
            new UserConnectionManager(),
            config);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.Name, "demo@serialloghub.local") }, "TestAuth"))
            }
        };

        var result = await controller.TestConnection(
            new SerialPortModel { SelectedPort = port, SelectedBaudRate = 9600 },
            CancellationToken.None);

        string json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("\"status\":\"Error\"", json);
    }
}
