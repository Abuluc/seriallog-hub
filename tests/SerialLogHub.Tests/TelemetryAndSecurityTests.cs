using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SerialLogHub.Api.Controllers;
using SerialLogHub.Api.Data;
using SerialLogHub.Api.Entities;
using SerialLogHub.Api.Models;
using SerialLogHub.Api.Services;
using SerialLogHub.Desktop.Services;
using Xunit;

namespace SerialLogHub.Tests;

public sealed class TelemetryAndSecurityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AnomalyDetectionEngine _anomalyEngine;
    private readonly PasswordSecurityService _passwordService;

    public TelemetryAndSecurityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _anomalyEngine = new AnomalyDetectionEngine();
        _passwordService = new PasswordSecurityService();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task TelemetryController_IngestHistoryAndCompare_PersistsAndDeduplicatesRecords()
    {
        var controller = new TelemetryController(_db, _anomalyEngine, NullLogger<TelemetryController>.Instance);

        string payloadA = string.Join("\n", new[]
        {
            "CMD,OK,LSD,",
            "CMD,OK,22.10,48.00,2026,11,22,10,00,00",
            "CMD,OK,22.20,48.50,2026,11,22,10,00,20",
            "CMD,OK,35.90,49.00,2026,11,22,10,00,40" // sudden spike
        });

        var ingestA = await controller.IngestTelemetry(new TelemetryIngestRequest
        {
            Email = "ops@example.com",
            PortName = "COM99",
            DeviceName = "Vault_A",
            RawCsvPayload = payloadA
        });
        Assert.IsType<OkObjectResult>(ingestA);

        // Re-ingesting identical timestamps must deduplicate (0 new rows inserted)
        var duplicateA = await controller.IngestTelemetry(new TelemetryIngestRequest
        {
            Email = "ops@example.com",
            PortName = "COM99",
            DeviceName = "Vault_A",
            RawCsvPayload = payloadA
        });
        var dupOk = Assert.IsType<OkObjectResult>(duplicateA);
        Assert.Contains("\"insertedCount\":0", System.Text.Json.JsonSerializer.Serialize(dupOk.Value));

        // Ingest second device for comparison
        await controller.IngestTelemetry(new TelemetryIngestRequest
        {
            Email = "ops@example.com",
            PortName = "COM98",
            DeviceName = "Vault_B",
            RawCsvPayload = "CMD,OK,19.50,55.00,2026,11,22,10,00,00\nCMD,OK,19.60,55.20,2026,11,22,10,00,20"
        });

        var historyRes = await controller.GetHistory("ops@example.com", anomaliesOnly: true);
        var histOk = Assert.IsType<OkObjectResult>(historyRes);
        string histJson = System.Text.Json.JsonSerializer.Serialize(histOk.Value);
        Assert.Contains("\"totalCount\":1", histJson);

        var devices = await _db.Devices.Where(d => d.UserEmail == "ops@example.com").ToListAsync();
        Assert.Equal(2, devices.Count);

        var cmpRes = await controller.CompareDevices("ops@example.com", devices[0].Id, devices[1].Id);
        var cmpOk = Assert.IsType<OkObjectResult>(cmpRes);
        string cmpJson = System.Text.Json.JsonSerializer.Serialize(cmpOk.Value);
        Assert.Contains("Vault_A", cmpJson);
        Assert.Contains("Vault_B", cmpJson);
    }

    [Fact]
    public async Task BOLA_OperatorScopeGuard_BlocksCrossUserAccessAcrossControllers()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[OperatorScopeGuard.OperatorEmailHeaderName] = "alice@example.com";

        var userController = new UserController(_db, NullLogger<UserController>.Instance, _anomalyEngine)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var telemetryController = new TelemetryController(_db, _anomalyEngine, NullLogger<TelemetryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        // Alice attempting to read/write Bob's resources must receive 403 Forbidden
        var portsRes = Assert.IsType<ObjectResult>(await userController.GetPorts("bob@example.com"));
        Assert.Equal(StatusCodes.Status403Forbidden, portsRes.StatusCode);

        var cmdRes = Assert.IsType<ObjectResult>(await userController.SendCommand(new SendCommandRequest
        {
            Email = "bob@example.com",
            SelectedPort = "COM99",
            Message = "WLE1"
        }));
        Assert.Equal(StatusCodes.Status403Forbidden, cmdRes.StatusCode);

        var pollRes = Assert.IsType<ObjectResult>(await userController.GetCommands("bob@example.com"));
        Assert.Equal(StatusCodes.Status403Forbidden, pollRes.StatusCode);

        var histRes = Assert.IsType<ObjectResult>(await userController.GetCommandHistory("bob@example.com"));
        Assert.Equal(StatusCodes.Status403Forbidden, histRes.StatusCode);

        var devRes = Assert.IsType<ObjectResult>(await telemetryController.GetDevices("bob@example.com"));
        Assert.Equal(StatusCodes.Status403Forbidden, devRes.StatusCode);

        var telHistRes = Assert.IsType<ObjectResult>(await telemetryController.GetHistory("bob@example.com"));
        Assert.Equal(StatusCodes.Status403Forbidden, telHistRes.StatusCode);
    }

    [Fact]
    public async Task AuthController_ConfirmPasswordReset_EnforcesTokenExpirationAndFixedTimeMatch()
    {
        var config = new ConfigurationBuilder().Build();
        var authController = new AuthController(_db, _passwordService, config, NullLogger<AuthController>.Instance);

        _db.Users.Add(new UserEntity
        {
            Email = "reset@example.com",
            Username = "reset_user",
            HashedPassword = _passwordService.HashForStorage("reset@example.com", "OldPass123!"),
            IsEmailVerified = true,
            ResetPasswordToken = "valid-secret-token-123",
            TokenExpiryTime = DateTime.UtcNow.AddMinutes(30)
        });
        await _db.SaveChangesAsync();

        // Wrong token -> 401 Unauthorized
        var badTokenRes = await authController.ConfirmPasswordReset(new ConfirmPasswordResetRequest
        {
            Email = "reset@example.com",
            ResetToken = "wrong-token",
            NewPassword = "NewPassword123!"
        });
        Assert.IsType<UnauthorizedObjectResult>(badTokenRes);

        // Correct token -> 200 OK & password updated & token invalidated
        var okRes = await authController.ConfirmPasswordReset(new ConfirmPasswordResetRequest
        {
            Email = "reset@example.com",
            ResetToken = "valid-secret-token-123",
            NewPassword = "NewPassword123!"
        });
        Assert.IsType<OkObjectResult>(okRes);

        // Reusing the same token a second time -> 401 Unauthorized
        var replayRes = await authController.ConfirmPasswordReset(new ConfirmPasswordResetRequest
        {
            Email = "reset@example.com",
            ResetToken = "valid-secret-token-123",
            NewPassword = "AnotherPassword123!"
        });
        Assert.IsType<UnauthorizedObjectResult>(replayRes);
    }

    [Fact]
    public async Task AuthController_Login_AcceptsEitherEmailOrUsername()
    {
        var config = new ConfigurationBuilder().Build();
        var authController = new AuthController(_db, _passwordService, config, NullLogger<AuthController>.Instance);

        _db.Users.Add(new UserEntity
        {
            Email = "operator@serialloghub.local",
            Username = "field_operator",
            HashedPassword = _passwordService.HashForStorage("operator@serialloghub.local", "SecretPass123!"),
            IsEmailVerified = true
        });
        await _db.SaveChangesAsync();

        // 1. Login with Email only
        var emailLogin = await authController.Login(new UserAuthRequest
        {
            Email = "operator@serialloghub.local",
            HashedPassword = "SecretPass123!"
        });
        Assert.IsType<OkObjectResult>(emailLogin);

        // 2. Login with Username only (sent in Email or Username field)
        var usernameInEmailField = await authController.Login(new UserAuthRequest
        {
            Email = "field_operator",
            HashedPassword = "SecretPass123!"
        });
        var okUser = Assert.IsType<OkObjectResult>(usernameInEmailField);
        Assert.Contains("operator@serialloghub.local", System.Text.Json.JsonSerializer.Serialize(okUser.Value));

        var usernameOnly = await authController.Login(new UserAuthRequest
        {
            Username = "field_operator",
            HashedPassword = "SecretPass123!"
        });
        Assert.IsType<OkObjectResult>(usernameOnly);
    }

    [Fact]
    public async Task UserController_TransitionsCommandLifecycle_AndAutoPersistsWlbTelemetry()
    {
        var controller = new UserController(_db, NullLogger<UserController>.Instance, _anomalyEngine);

        // 1. SendCommand -> Pending
        var sendRes = Assert.IsType<OkObjectResult>(await controller.SendCommand(new SendCommandRequest
        {
            Email = "ops@example.com",
            SelectedPort = "COM99",
            BaudRate = 9600,
            Message = "WLB",
            CommandID = "wlb-lifecycle-1"
        }));
        var queuedDto = Assert.IsType<CommandResponseDto>(sendRes.Value);
        Assert.Equal(CommandLifecycleStatus.Pending, queuedDto.Status);

        // 2. GetCommands -> Dispatched
        var pollRes = Assert.IsType<OkObjectResult>(await controller.GetCommands("ops@example.com"));
        var dispatchedDto = Assert.IsType<CommandResponseDto>(pollRes.Value);
        Assert.Equal(CommandLifecycleStatus.Dispatched, dispatchedDto.Status);
        Assert.NotNull(dispatchedDto.DispatchedAt);
        Assert.Equal(1, dispatchedDto.AttemptCount);

        // 3. UpdateCommand with WLB payload -> Completed + TelemetryRecords auto-created
        string wlbDump = "CMD,OK,LSD,\nCMD,OK,22.40,48.10,2026,11,22,12,00,00\nCMD,OK,22.50,48.20,2026,11,22,12,00,20";
        var updateRes = Assert.IsType<OkObjectResult>(await controller.UpdateCommand(new UpdateCommandRequest
        {
            Email = "ops@example.com",
            CommandID = "wlb-lifecycle-1",
            Response = wlbDump
        }));
        var completedDto = Assert.IsType<CommandResponseDto>(updateRes.Value);
        Assert.Equal(CommandLifecycleStatus.Completed, completedDto.Status);
        Assert.NotNull(completedDto.CompletedAt);

        int storedTelemetry = await _db.TelemetryRecords.CountAsync(r => r.UserEmail == "ops@example.com");
        Assert.Equal(2, storedTelemetry);
    }

    [Theory]
    [InlineData("WLE1", true)]
    [InlineData("WLH1,2026,11,22,12,00,00", true)]
    [InlineData("WLS0,2026,11,22,12,00", true)]
    [InlineData("WLM,10,30,20,80", true)]
    [InlineData("WLA20", true)]
    [InlineData("WLV1", true)]
    [InlineData("WLG, VAULT_1", true)]
    [InlineData("TEST", false)]
    [InlineData("WLB", false)]
    [InlineData("WLJ", false)]
    [InlineData("WLW", false)]
    [InlineData("WLH0", false)]
    public void BridgeEngine_IsNonIdempotentPhysicalCommand_ClassifiesCommandsAccurately(string cmd, bool expected)
    {
        Assert.Equal(expected, BridgeEngine.IsNonIdempotentPhysicalCommand(cmd));
    }
}
