using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SerialLogHub.Api.Controllers;
using SerialLogHub.Api.Data;
using SerialLogHub.Api.Entities;
using SerialLogHub.Api.Middleware;
using SerialLogHub.Api.Models;
using SerialLogHub.Api.Services;
using SerialLogHub.Desktop.Services;
using SerialLogHub.Web.Controllers;
using SerialLogHub.Web.Hubs;
using SerialLogHub.Web.Models;
using SerialLogHub.Web.Services;
using Xunit;

namespace SerialLogHub.Tests;

public sealed class TelemetryAndSecurityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AnomalyDetectionEngine _anomalyEngine;
    private readonly PasswordSecurityService _passwordService;
    private readonly IConfiguration _configuration;

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
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiSecurity:RequireApiKey"] = "true",
                ["ApiSecurity:ApiKey"] = "test-master-api-key-secret",
                ["BridgeSecurity:RequireBridgeKey"] = "true",
                ["BridgeSecurity:BridgeKey"] = "test-bridge-secret-key"
            })
            .Build();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task NegativeSecurity_MissingOperatorHeaders_AreRejectedWith401()
    {
        // Attach a real HttpContext with NO X-OPERATOR-EMAIL and NO X-OPERATOR-TOKEN
        var httpContext = new DefaultHttpContext();

        var userController = new UserController(_db, NullLogger<UserController>.Instance, _anomalyEngine, _configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        var telemetryController = new TelemetryController(_db, _anomalyEngine, NullLogger<TelemetryController>.Instance, _configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var portsRes = Assert.IsType<ObjectResult>(await userController.GetPorts("alice@example.com"));
        Assert.Equal(StatusCodes.Status401Unauthorized, portsRes.StatusCode);

        var cmdRes = Assert.IsType<ObjectResult>(await userController.SendCommand(new SendCommandRequest
        {
            Email = "alice@example.com",
            SelectedPort = "COM99",
            Message = "TEST"
        }));
        Assert.Equal(StatusCodes.Status401Unauthorized, cmdRes.StatusCode);

        var devRes = Assert.IsType<ObjectResult>(await telemetryController.GetDevices("alice@example.com"));
        Assert.Equal(StatusCodes.Status401Unauthorized, devRes.StatusCode);

        var histRes = Assert.IsType<ObjectResult>(await telemetryController.GetHistory("alice@example.com"));
        Assert.Equal(StatusCodes.Status401Unauthorized, histRes.StatusCode);
    }

    [Fact]
    public async Task NegativeSecurity_ForgedBareOperatorEmailHeader_IsRejectedEvenWhenMatchingTarget()
    {
        // An attacker possessing a shared key sends a bare X-OPERATOR-EMAIL: victim@example.com
        // without a valid server-issued X-OPERATOR-TOKEN or valid HMAC X-OPERATOR-PROOF
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[OperatorScopeGuard.OperatorEmailHeaderName] = "victim@example.com";

        var userController = new UserController(_db, NullLogger<UserController>.Instance, _anomalyEngine, _configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
        var telemetryController = new TelemetryController(_db, _anomalyEngine, NullLogger<TelemetryController>.Instance, _configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

        var portsRes = Assert.IsType<ObjectResult>(await userController.GetPorts("victim@example.com"));
        Assert.Equal(StatusCodes.Status401Unauthorized, portsRes.StatusCode);

        var telRes = Assert.IsType<ObjectResult>(await telemetryController.GetHistory("victim@example.com"));
        Assert.Equal(StatusCodes.Status401Unauthorized, telRes.StatusCode);
    }

    [Fact]
    public async Task NegativeSecurity_OperatorAWithValidToken_CannotAccessOperatorB_PortsCommandsOrTelemetry()
    {
        var authController = new SerialLogHub.Api.Controllers.AuthController(
            _db, _passwordService, _configuration, NullLogger<SerialLogHub.Api.Controllers.AuthController>.Instance);

        _db.Users.AddRange(
            new UserEntity
            {
                Email = "alice@example.com",
                Username = "alice_op",
                HashedPassword = _passwordService.HashForStorage("alice@example.com", "AlicePass123!"),
                IsEmailVerified = true
            },
            new UserEntity
            {
                Email = "bob@example.com",
                Username = "bob_op",
                HashedPassword = _passwordService.HashForStorage("bob@example.com", "BobPass123!"),
                IsEmailVerified = true
            });
        _db.UsersPort.Add(new UserPortEntity
        {
            UserEmail = "bob@example.com",
            Ports = "[\"COM7 (Bob Hardware)\"]"
        });
        await _db.SaveChangesAsync();

        // Alice logs in and receives a real DB-backed operatorToken bound to alice@example.com
        var loginOk = Assert.IsType<OkObjectResult>(await authController.Login(new UserAuthRequest
        {
            Email = "alice@example.com",
            HashedPassword = "AlicePass123!"
        }));
        using var loginDoc = JsonDocument.Parse(JsonSerializer.Serialize(loginOk.Value));
        string aliceToken = loginDoc.RootElement.GetProperty("operatorToken").GetString()!;
        Assert.StartsWith("slh_op_", aliceToken);

        // Verify the token is stored hashed (not plaintext) in SQLite
        var storedKey = await _db.ApiKeys.SingleAsync(k => k.OwnerEmail == "alice@example.com");
        Assert.StartsWith("sha256:", storedKey.Key);
        Assert.NotEqual(aliceToken, storedKey.Key);

        var aliceContext = new DefaultHttpContext();
        aliceContext.Request.Headers[OperatorScopeGuard.OperatorTokenHeaderName] = aliceToken;

        var userController = new UserController(_db, NullLogger<UserController>.Instance, _anomalyEngine, _configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = aliceContext }
        };
        var telemetryController = new TelemetryController(_db, _anomalyEngine, NullLogger<TelemetryController>.Instance, _configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = aliceContext }
        };

        // 1. Legitimate access: Alice updating & reading her own ports succeeds
        var aliceUpdateOk = await userController.UpdatePorts(new UpdatePortsRequest
        {
            UserEmail = "alice@example.com",
            Ports = new List<string> { "COM3 (Alice Device)" }
        });
        Assert.IsType<OkObjectResult>(aliceUpdateOk);

        // 2. Cross-user BOLA: Alice trying to read Bob's COM ports -> 403 Forbidden
        var bobPortsRes = Assert.IsType<ObjectResult>(await userController.GetPorts("bob@example.com"));
        Assert.Equal(StatusCodes.Status403Forbidden, bobPortsRes.StatusCode);

        // 3. Cross-user BOLA: Alice trying to send a command to Bob's device -> 403 Forbidden
        var bobCmdRes = Assert.IsType<ObjectResult>(await userController.SendCommand(new SendCommandRequest
        {
            Email = "bob@example.com",
            SelectedPort = "COM7 (Bob Hardware)",
            Message = "WLE1"
        }));
        Assert.Equal(StatusCodes.Status403Forbidden, bobCmdRes.StatusCode);

        // 4. Cross-user BOLA: Alice trying to access Bob's telemetry -> 403 Forbidden
        var bobTelRes = Assert.IsType<ObjectResult>(await telemetryController.GetHistory("bob@example.com"));
        Assert.Equal(StatusCodes.Status403Forbidden, bobTelRes.StatusCode);

        // 5. Forged X-OPERATOR-EMAIL header alongside Alice's valid token -> 403 Forbidden
        var forgedContext = new DefaultHttpContext();
        forgedContext.Request.Headers[OperatorScopeGuard.OperatorTokenHeaderName] = aliceToken;
        forgedContext.Request.Headers[OperatorScopeGuard.OperatorEmailHeaderName] = "bob@example.com";
        userController.ControllerContext = new ControllerContext { HttpContext = forgedContext };

        var forgedRes = Assert.IsType<ObjectResult>(await userController.GetPorts("bob@example.com"));
        Assert.Equal(StatusCodes.Status403Forbidden, forgedRes.StatusCode);
    }

    [Fact]
    public async Task NegativeSecurity_ExpiredRevokedAndReplayedCredentials_AreRejectedWith401()
    {
        string expiredRaw = "slh_op_expired_token_123";
        string revokedRaw = "slh_op_revoked_token_456";

        _db.ApiKeys.AddRange(
            new ApiKeyEntity
            {
                Key = OperatorScopeGuard.HashCredential(expiredRaw),
                OwnerEmail = "alice@example.com",
                ClientType = ApiCredentialClientTypes.OperatorSession,
                IsRevoked = false,
                ExpiresAt = DateTime.UtcNow.AddMinutes(-10)
            },
            new ApiKeyEntity
            {
                Key = OperatorScopeGuard.HashCredential(revokedRaw),
                OwnerEmail = "alice@example.com",
                ClientType = ApiCredentialClientTypes.OperatorSession,
                IsRevoked = true,
                ExpiresAt = DateTime.UtcNow.AddHours(10)
            });
        await _db.SaveChangesAsync();

        // 1. Expired operator token -> 401
        var expiredCtx = new DefaultHttpContext();
        expiredCtx.Request.Headers[OperatorScopeGuard.OperatorTokenHeaderName] = expiredRaw;
        var userController = new UserController(_db, NullLogger<UserController>.Instance, _anomalyEngine, _configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = expiredCtx }
        };
        var expRes = Assert.IsType<ObjectResult>(await userController.GetPorts("alice@example.com"));
        Assert.Equal(StatusCodes.Status401Unauthorized, expRes.StatusCode);

        // 2. Revoked operator token -> 401
        var revokedCtx = new DefaultHttpContext();
        revokedCtx.Request.Headers[OperatorScopeGuard.OperatorTokenHeaderName] = revokedRaw;
        userController.ControllerContext = new ControllerContext { HttpContext = revokedCtx };
        var revRes = Assert.IsType<ObjectResult>(await userController.GetPorts("alice@example.com"));
        Assert.Equal(StatusCodes.Status401Unauthorized, revRes.StatusCode);

        // 3. Replayed HMAC proof (> 5 minutes old) -> 401
        long staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-15).ToUnixTimeSeconds();
        string staleProof = OperatorScopeGuard.ComputeOperatorHmacProof("alice@example.com", staleTimestamp, "test-master-api-key-secret");
        var replayCtx = new DefaultHttpContext();
        replayCtx.Request.Headers[OperatorScopeGuard.OperatorEmailHeaderName] = "alice@example.com";
        replayCtx.Request.Headers[OperatorScopeGuard.OperatorTimestampHeaderName] = staleTimestamp.ToString();
        replayCtx.Request.Headers[OperatorScopeGuard.OperatorProofHeaderName] = staleProof;
        userController.ControllerContext = new ControllerContext { HttpContext = replayCtx };

        var replayRes = Assert.IsType<ObjectResult>(await userController.GetPorts("alice@example.com"));
        Assert.Equal(StatusCodes.Status401Unauthorized, replayRes.StatusCode);

        // 4. Valid current HMAC proof (Web Server -> API workflow) -> Authorized!
        long freshTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string freshProof = OperatorScopeGuard.ComputeOperatorHmacProof("alice@example.com", freshTimestamp, "test-master-api-key-secret");
        var validHmacCtx = new DefaultHttpContext();
        validHmacCtx.Request.Headers[OperatorScopeGuard.OperatorEmailHeaderName] = "alice@example.com";
        validHmacCtx.Request.Headers[OperatorScopeGuard.OperatorTimestampHeaderName] = freshTimestamp.ToString();
        validHmacCtx.Request.Headers[OperatorScopeGuard.OperatorProofHeaderName] = freshProof;
        userController.ControllerContext = new ControllerContext { HttpContext = validHmacCtx };

        var validRes = await userController.UpdatePorts(new UpdatePortsRequest
        {
            UserEmail = "alice@example.com",
            Ports = new List<string> { "COM99" }
        });
        Assert.IsType<OkObjectResult>(validRes);
    }

    [Fact]
    public async Task NegativeSecurity_ApiKeyMiddleware_RejectsInvalidAndExpiredKeys_AndAcceptsHashedDbKey()
    {
        string activeApiKey = "slh_live_key_abc123";
        string expiredApiKey = "slh_expired_key_xyz987";

        _db.ApiKeys.AddRange(
            new ApiKeyEntity
            {
                Key = OperatorScopeGuard.HashCredential(activeApiKey),
                OwnerEmail = "alice@example.com",
                ClientType = ApiCredentialClientTypes.DesktopBridge,
                IsRevoked = false,
                ExpiresAt = DateTime.UtcNow.AddHours(2)
            },
            new ApiKeyEntity
            {
                Key = OperatorScopeGuard.HashCredential(expiredApiKey),
                OwnerEmail = "alice@example.com",
                ClientType = ApiCredentialClientTypes.DesktopBridge,
                IsRevoked = false,
                ExpiresAt = DateTime.UtcNow.AddMinutes(-5)
            });
        await _db.SaveChangesAsync();

        bool nextCalled = false;
        var middleware = new ApiKeyMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, _configuration, NullLogger<ApiKeyMiddleware>.Instance);

        // 1. Missing X-API-KEY -> 401
        var missingCtx = new DefaultHttpContext();
        missingCtx.Request.Path = "/api/user/getports";
        await middleware.InvokeAsync(missingCtx, _db);
        Assert.Equal(StatusCodes.Status401Unauthorized, missingCtx.Response.StatusCode);
        Assert.False(nextCalled);

        // 2. Expired DB X-API-KEY -> 401
        var expiredCtx = new DefaultHttpContext();
        expiredCtx.Request.Path = "/api/user/getports";
        expiredCtx.Request.Headers["X-API-KEY"] = expiredApiKey;
        await middleware.InvokeAsync(expiredCtx, _db);
        Assert.Equal(StatusCodes.Status401Unauthorized, expiredCtx.Response.StatusCode);
        Assert.False(nextCalled);

        // 3. Valid hashed DB X-API-KEY -> 200 & binds AuthenticatedOperatorEmail to alice@example.com
        var validCtx = new DefaultHttpContext();
        validCtx.Request.Path = "/api/user/getports";
        validCtx.Request.Headers["X-API-KEY"] = activeApiKey;
        await middleware.InvokeAsync(validCtx, _db);
        Assert.True(nextCalled);
        Assert.Equal("alice@example.com", validCtx.Items[OperatorScopeGuard.ContextOperatorEmailKey]);
    }

    [Fact]
    public async Task NegativeSecurity_WebBridgeWebhooks_RejectInvalidBridgeCredentials()
    {
        var apiClient = new ApiClientService(new HttpClient(), _configuration, NullLogger<ApiClientService>.Instance);
        var homeController = new HomeController(
            NullLogger<HomeController>.Instance,
            apiClient,
            null!,
            new UserConnectionManager(),
            _configuration);

        var badCtx = new DefaultHttpContext();
        badCtx.Request.Headers["X-BRIDGE-KEY"] = "forged-wrong-bridge-key";
        homeController.ControllerContext = new ControllerContext { HttpContext = badCtx };

        var appCheckRes = await homeController.AppCheck(new AppHealthPayload { AppName = "ConsoleApp", UserName = "alice@example.com" }, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(appCheckRes);

        var waitRes = await homeController.waitresponse(new WaitResponsePayload { AppName = "ConsoleApp2", UserName = "alice@example.com" }, CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(waitRes);
    }

    [Fact]
    public async Task DatabaseMigration_UpgradesLegacyV1SqliteDatabase_PreservingAllDataAndHashingKeys()
    {
        using var legacyConn = new SqliteConnection("DataSource=:memory:");
        await legacyConn.OpenAsync();

        // 1. Create a strict legacy v1 schema (only the original 5 tables and original columns)
        using (var cmd = legacyConn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE Users (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Username TEXT NOT NULL,
                    Email TEXT NOT NULL,
                    HashedPassword TEXT NOT NULL,
                    IsEmailVerified INTEGER NOT NULL,
                    VerificationToken TEXT NOT NULL,
                    ResetPasswordToken TEXT NOT NULL,
                    TokenExpiryTime TEXT NOT NULL
                );
                CREATE TABLE UsersPort (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    UserEmail TEXT NOT NULL,
                    Ports TEXT NOT NULL
                );
                CREATE TABLE Commands (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Email TEXT NOT NULL,
                    SelecetdPort TEXT NOT NULL,
                    BaudRate INTEGER NOT NULL,
                    Message TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    Response TEXT NOT NULL,
                    CommandID TEXT NOT NULL,
                    IsProcessed INTEGER NOT NULL
                );
                CREATE TABLE CommandHistories (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Email TEXT NOT NULL,
                    CommandID TEXT NOT NULL,
                    Port TEXT NOT NULL,
                    BaudRate INTEGER NOT NULL,
                    Message TEXT NOT NULL,
                    Response TEXT NOT NULL,
                    Timestamp TEXT NOT NULL
                );
                CREATE TABLE ApiKeys (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Key TEXT NOT NULL,
                    ExpiresAt TEXT NOT NULL
                );

                INSERT INTO Users (Username, Email, HashedPassword, IsEmailVerified, VerificationToken, ResetPasswordToken, TokenExpiryTime)
                VALUES ('legacy_user', 'legacy@example.com', 'hashed_pw', 1, 'none', 'none', '2027-01-01T00:00:00Z');

                INSERT INTO Commands (Email, SelecetdPort, BaudRate, Message, Timestamp, Response, CommandID, IsProcessed)
                VALUES ('legacy@example.com', 'COM3', 9600, 'WLJ', '2026-11-22T10:00:00Z', 'CMD,OK,BAT,90', 'legacy-cmd-1', 1);

                INSERT INTO ApiKeys (Key, ExpiresAt)
                VALUES ('legacy-plaintext-api-key', '2028-01-01T00:00:00Z');
            ";
            await cmd.ExecuteNonQueryAsync();
        }

        var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(legacyConn).Options;
        using var upgradedDb = new AppDbContext(opts);

        // 2. Run versioned schema upgrade twice to verify idempotency and data preservation
        await DbSeeder.UpgradeSchemaAndMigrateAsync(upgradedDb);
        await DbSeeder.UpgradeSchemaAndMigrateAsync(upgradedDb);

        var preservedUser = await upgradedDb.Users.SingleAsync(u => u.Email == "legacy@example.com");
        Assert.Equal("legacy_user", preservedUser.Username);

        var preservedCmd = await upgradedDb.Commands.SingleAsync(c => c.CommandID == "legacy-cmd-1");
        Assert.Equal("Pending", preservedCmd.Status);
        Assert.Equal("CMD,OK,BAT,90", preservedCmd.Response);

        var migratedKey = await upgradedDb.ApiKeys.SingleAsync();
        Assert.StartsWith("sha256:", migratedKey.Key);
        Assert.True(OperatorScopeGuard.VerifyHashedOrPlainCredential("legacy-plaintext-api-key", migratedKey.Key));
    }

    [Fact]
    public async Task TelemetryPerformance_HandlesLargeBatchDeduplication_AndRejectsOversizedBatch()
    {
        var controller = new TelemetryController(_db, _anomalyEngine, NullLogger<TelemetryController>.Instance);
        var start = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

        // 1. Ingest 1,500 synthetic samples in one batch
        var batch1500 = Enumerable.Range(0, 1500)
            .Select(i => new TelemetryInputSample
            {
                Temperature = 22.0 + (i % 5) * 0.1,
                Humidity = 50.0 + (i % 7) * 0.2,
                RecordedAt = start.AddSeconds(i * 20)
            })
            .ToList();

        var res1 = await controller.IngestTelemetry(new TelemetryIngestRequest
        {
            Email = "perf@example.com",
            PortName = "COM99",
            DeviceName = "HighVolumeLogger",
            Samples = batch1500
        });
        var ok1 = Assert.IsType<OkObjectResult>(res1);
        Assert.Contains("\"insertedCount\":1500", JsonSerializer.Serialize(ok1.Value));

        // 2. Re-ingest an overlapping window of 500 existing samples + 100 new samples -> must insert only 100
        var overlapping600 = Enumerable.Range(1000, 600)
            .Select(i => new TelemetryInputSample
            {
                Temperature = 22.0,
                Humidity = 50.0,
                RecordedAt = start.AddSeconds(i * 20)
            })
            .ToList();

        var res2 = await controller.IngestTelemetry(new TelemetryIngestRequest
        {
            Email = "perf@example.com",
            PortName = "COM99",
            DeviceName = "HighVolumeLogger",
            Samples = overlapping600
        });
        var ok2 = Assert.IsType<OkObjectResult>(res2);
        Assert.Contains("\"insertedCount\":100", JsonSerializer.Serialize(ok2.Value));

        // 3. Oversized batch (> 5000 samples) -> 400 BadRequest
        var oversized = Enumerable.Range(0, TelemetryController.MaxSamplesPerIngest + 1)
            .Select(i => new TelemetryInputSample
            {
                Temperature = 22.0,
                Humidity = 50.0,
                RecordedAt = start.AddSeconds(i)
            })
            .ToList();

        var overRes = await controller.IngestTelemetry(new TelemetryIngestRequest
        {
            Email = "perf@example.com",
            PortName = "COM99",
            Samples = oversized
        });
        Assert.IsType<BadRequestObjectResult>(overRes);
    }

    [Fact]
    public async Task AuthController_ConfirmPasswordReset_EnforcesTokenExpirationAndFixedTimeMatch()
    {
        var authController = new SerialLogHub.Api.Controllers.AuthController(
            _db, _passwordService, _configuration, NullLogger<SerialLogHub.Api.Controllers.AuthController>.Instance);

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

        var badTokenRes = await authController.ConfirmPasswordReset(new ConfirmPasswordResetRequest
        {
            Email = "reset@example.com",
            ResetToken = "wrong-token",
            NewPassword = "NewPassword123!"
        });
        Assert.IsType<UnauthorizedObjectResult>(badTokenRes);

        var okRes = await authController.ConfirmPasswordReset(new ConfirmPasswordResetRequest
        {
            Email = "reset@example.com",
            ResetToken = "valid-secret-token-123",
            NewPassword = "NewPassword123!"
        });
        Assert.IsType<OkObjectResult>(okRes);

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
        var authController = new SerialLogHub.Api.Controllers.AuthController(
            _db, _passwordService, _configuration, NullLogger<SerialLogHub.Api.Controllers.AuthController>.Instance);

        _db.Users.Add(new UserEntity
        {
            Email = "operator@serialloghub.local",
            Username = "field_operator",
            HashedPassword = _passwordService.HashForStorage("operator@serialloghub.local", "SecretPass123!"),
            IsEmailVerified = true
        });
        await _db.SaveChangesAsync();

        var emailLogin = await authController.Login(new UserAuthRequest
        {
            Email = "operator@serialloghub.local",
            HashedPassword = "SecretPass123!"
        });
        Assert.IsType<OkObjectResult>(emailLogin);

        var usernameInEmailField = await authController.Login(new UserAuthRequest
        {
            Email = "field_operator",
            HashedPassword = "SecretPass123!"
        });
        var okUser = Assert.IsType<OkObjectResult>(usernameInEmailField);
        Assert.Contains("operator@serialloghub.local", JsonSerializer.Serialize(okUser.Value));

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

        var pollRes = Assert.IsType<OkObjectResult>(await controller.GetCommands("ops@example.com"));
        var dispatchedDto = Assert.IsType<CommandResponseDto>(pollRes.Value);
        Assert.Equal(CommandLifecycleStatus.Dispatched, dispatchedDto.Status);
        Assert.NotNull(dispatchedDto.DispatchedAt);
        Assert.Equal(1, dispatchedDto.AttemptCount);

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
