using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SerialLogHub.Api.Entities;
using SerialLogHub.Api.Services;

namespace SerialLogHub.Api.Data;

public static class DbSeeder
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var passwordService = scope.ServiceProvider.GetRequiredService<PasswordSecurityService>();
        var anomalyEngine = scope.ServiceProvider.GetRequiredService<AnomalyDetectionEngine>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

        await db.Database.EnsureCreatedAsync();
        await EnsureSchemaUpgradedAsync(db, logger);

        string configuredApiKey = config["ApiSecurity:ApiKey"] ?? "dev-serialloghub-api-key";
        if (!await db.ApiKeys.AnyAsync(k => k.Key == configuredApiKey))
        {
            db.ApiKeys.Add(new ApiKeyEntity
            {
                Key = configuredApiKey,
                ExpiresAt = DateTime.UtcNow.AddYears(5)
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded API key into ApiKeys table.");
        }

        bool demoEnabled = config.GetValue("DemoMode:Enabled", false);
        if (!demoEnabled)
        {
            return;
        }

        string demoEmail = (config["DemoMode:DemoEmail"] ?? "demo@serialloghub.local").Trim().ToLowerInvariant();
        string demoUsername = config["DemoMode:DemoUsername"] ?? "demo_operator";
        string demoPassword = config["DemoMode:DemoPassword"] ?? "Demo1234!";
        string defaultVirtualPort = config["DemoMode:DefaultVirtualPort"] ?? "COM99 (Virtual DataLogger - Demo)";

        if (!await db.Users.AnyAsync(u => u.Email.ToLower() == demoEmail))
        {
            db.Users.Add(new UserEntity
            {
                Username = demoUsername,
                Email = demoEmail,
                HashedPassword = passwordService.HashForStorage(demoEmail, demoPassword),
                IsEmailVerified = true,
                VerificationToken = "demo-verified",
                ResetPasswordToken = "None",
                TokenExpiryTime = DateTime.UtcNow.AddYears(1)
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded demo operator account: {Email}", demoEmail);
        }

        if (!await db.UsersPort.AnyAsync(p => p.UserEmail.ToLower() == demoEmail))
        {
            db.UsersPort.Add(new UserPortEntity
            {
                UserEmail = demoEmail,
                Ports = JsonSerializer.Serialize(new List<string> { defaultVirtualPort })
            });
            await db.SaveChangesAsync();
        }

        await SeedDemoTelemetryIfEmptyAsync(db, anomalyEngine, demoEmail, defaultVirtualPort, logger);
    }

    private static async Task EnsureSchemaUpgradedAsync(AppDbContext db, ILogger logger)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS Devices (
                    Id INTEGER NOT NULL CONSTRAINT PK_Devices PRIMARY KEY AUTOINCREMENT,
                    UserEmail TEXT NOT NULL,
                    DeviceName TEXT NOT NULL,
                    PortName TEXT NOT NULL,
                    SerialNumber TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    LastSeenUtc TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS IX_Devices_UserEmail_PortName ON Devices (UserEmail, PortName);

                CREATE TABLE IF NOT EXISTS TelemetryRecords (
                    Id INTEGER NOT NULL CONSTRAINT PK_TelemetryRecords PRIMARY KEY AUTOINCREMENT,
                    DeviceId INTEGER NOT NULL,
                    UserEmail TEXT NOT NULL,
                    PortName TEXT NOT NULL,
                    Temperature REAL NOT NULL,
                    Humidity REAL NOT NULL,
                    RecordedAt TEXT NOT NULL,
                    IngestedAt TEXT NOT NULL,
                    IsAnomaly INTEGER NOT NULL,
                    AnomalySeverity TEXT NOT NULL,
                    AnomalyMethod TEXT NOT NULL,
                    AnomalyExplanation TEXT NOT NULL,
                    RawPayload TEXT NULL,
                    CONSTRAINT FK_TelemetryRecords_Devices_DeviceId FOREIGN KEY (DeviceId) REFERENCES Devices (Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_TelemetryRecords_UserEmail_RecordedAt ON TelemetryRecords (UserEmail, RecordedAt);
                CREATE INDEX IF NOT EXISTS IX_TelemetryRecords_DeviceId_RecordedAt ON TelemetryRecords (DeviceId, RecordedAt);
            ");

            // Add new Command lifecycle columns idempotently if an older SQLite file exists
            string[] alterStatements =
            {
                "ALTER TABLE Commands ADD COLUMN Status TEXT NOT NULL DEFAULT 'Pending';",
                "ALTER TABLE Commands ADD COLUMN DispatchedAt TEXT NULL;",
                "ALTER TABLE Commands ADD COLUMN CompletedAt TEXT NULL;",
                "ALTER TABLE Commands ADD COLUMN ErrorCategory TEXT NULL;",
                "ALTER TABLE Commands ADD COLUMN AttemptCount INTEGER NOT NULL DEFAULT 0;"
            };

            foreach (string sql in alterStatements)
            {
                try { await db.Database.ExecuteSqlRawAsync(sql); }
                catch { /* Column already exists */ }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Non-fatal warning during SQLite schema upgrade check.");
        }
    }

    private static async Task SeedDemoTelemetryIfEmptyAsync(
        AppDbContext db,
        AnomalyDetectionEngine anomalyEngine,
        string demoEmail,
        string primaryPort,
        ILogger logger)
    {
        if (await db.Devices.AnyAsync(d => d.UserEmail.ToLower() == demoEmail))
        {
            return;
        }

        var devA = new DeviceEntity
        {
            UserEmail = demoEmail,
            DeviceName = "DEMO_COLD_STORAGE_A1",
            PortName = primaryPort,
            SerialNumber = "VIRTUAL_LOGGER_V1",
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            LastSeenUtc = DateTime.UtcNow
        };

        var devB = new DeviceEntity
        {
            UserEmail = demoEmail,
            DeviceName = "COLD_VAULT_B2",
            PortName = "COM98 (Cold Chain Vault B2)",
            SerialNumber = "VIRTUAL_LOGGER_V2",
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            LastSeenUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        db.Devices.AddRange(devA, devB);
        await db.SaveChangesAsync();

        var start = DateTime.UtcNow.AddMinutes(-30);
        var rng = new Random(42);

        void SeedDeviceSamples(DeviceEntity dev, double baseTemp, double baseHum, int spikeIndex)
        {
            var samples = new List<TelemetryInputSample>(60);
            for (int i = 0; i < 60; i++)
            {
                double t = Math.Round(baseTemp + Math.Sin(i / 8.0) * 1.1 + (rng.NextDouble() - 0.5) * 0.3, 2);
                double h = Math.Round(baseHum + Math.Cos(i / 9.0) * 3.2 + (rng.NextDouble() - 0.5) * 0.6, 2);
                if (i == spikeIndex) t = Math.Round(baseTemp + 11.4, 2);
                samples.Add(new TelemetryInputSample
                {
                    Temperature = t,
                    Humidity = h,
                    RecordedAt = start.AddSeconds(i * 20)
                });
            }

            var evals = anomalyEngine.EvaluateSeries(samples);
            for (int i = 0; i < samples.Count; i++)
            {
                db.TelemetryRecords.Add(new TelemetryRecordEntity
                {
                    DeviceId = dev.Id,
                    UserEmail = demoEmail,
                    PortName = dev.PortName,
                    Temperature = samples[i].Temperature,
                    Humidity = samples[i].Humidity,
                    RecordedAt = samples[i].RecordedAt,
                    IngestedAt = DateTime.UtcNow,
                    IsAnomaly = evals[i].IsAnomaly,
                    AnomalySeverity = evals[i].Severity,
                    AnomalyMethod = evals[i].DetectionMethod,
                    AnomalyExplanation = evals[i].Explanation
                });
            }
        }

        SeedDeviceSamples(devA, 22.4, 48.5, 25);
        SeedDeviceSamples(devB, 19.2, 54.0, 42);
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded 2 persistent demo devices and 120 historical telemetry records for {Email}.", demoEmail);
    }
}
