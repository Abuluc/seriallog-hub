using System.Data.Common;
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
        await UpgradeSchemaAndMigrateAsync(db, logger);

        string configuredApiKey = config["ApiSecurity:ApiKey"] ?? "dev-serialloghub-api-key";
        string hashedConfiguredKey = OperatorScopeGuard.HashCredential(configuredApiKey);

        if (!await db.ApiKeys.AnyAsync(k => k.Key == hashedConfiguredKey || k.Key == configuredApiKey))
        {
            db.ApiKeys.Add(new ApiKeyEntity
            {
                Key = hashedConfiguredKey,
                OwnerEmail = "*",
                ClientType = ApiCredentialClientTypes.WebServer,
                IsRevoked = false,
                ExpiresAt = DateTime.UtcNow.AddYears(5)
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded hashed WebServer API credential into ApiKeys table.");
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

    /// <summary>
    /// Versioned, non-destructive SQLite schema migration runner that inspects actual table columns via
    /// PRAGMA table_info(...) before altering tables, recording applied migrations in __SchemaVersions
    /// without swallowing unexpected SQL failures.
    /// </summary>
    public static async Task UpgradeSchemaAndMigrateAsync(AppDbContext db, ILogger? logger = null)
    {
        var conn = db.Database.GetDbConnection();
        bool wasClosed = conn.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await conn.OpenAsync();
        }

        try
        {
            // 1. Ensure __SchemaVersions tracking table exists
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS __SchemaVersions (
                    VersionNumber INTEGER NOT NULL CONSTRAINT PK_SchemaVersions PRIMARY KEY,
                    MigrationName TEXT NOT NULL,
                    AppliedAtUtc TEXT NOT NULL
                );
            ");

            // 2. Migration 001: Devices & TelemetryRecords with Unique (DeviceId, RecordedAt) Index
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
                DROP INDEX IF EXISTS IX_TelemetryRecords_DeviceId_RecordedAt;
                CREATE UNIQUE INDEX IF NOT EXISTS IX_TelemetryRecords_DeviceId_RecordedAt ON TelemetryRecords (DeviceId, RecordedAt);
            ");

            // 3. Migration 002: Command lifecycle columns on Commands (inspected via PRAGMA table_info)
            var commandCols = await GetExistingColumnsAsync(conn, "Commands");
            await AddColumnIfMissingAsync(db, commandCols, "Commands", "Status", "TEXT NOT NULL DEFAULT 'Pending'");
            await AddColumnIfMissingAsync(db, commandCols, "Commands", "DispatchedAt", "TEXT NULL");
            await AddColumnIfMissingAsync(db, commandCols, "Commands", "CompletedAt", "TEXT NULL");
            await AddColumnIfMissingAsync(db, commandCols, "Commands", "ErrorCategory", "TEXT NULL");
            await AddColumnIfMissingAsync(db, commandCols, "Commands", "AttemptCount", "INTEGER NOT NULL DEFAULT 0");

            // 4. Migration 003: Scoped credential columns on ApiKeys (OwnerEmail, ClientType, IsRevoked)
            var apiKeyCols = await GetExistingColumnsAsync(conn, "ApiKeys");
            await AddColumnIfMissingAsync(db, apiKeyCols, "ApiKeys", "OwnerEmail", "TEXT NOT NULL DEFAULT '*'");
            await AddColumnIfMissingAsync(db, apiKeyCols, "ApiKeys", "ClientType", "TEXT NOT NULL DEFAULT 'WebServer'");
            await AddColumnIfMissingAsync(db, apiKeyCols, "ApiKeys", "IsRevoked", "INTEGER NOT NULL DEFAULT 0");

            // 5. Migrate any legacy plaintext keys in ApiKeys to SHA-256 hashes non-destructively
            var plaintextKeys = await db.ApiKeys
                .Where(k => !k.Key.StartsWith("sha256:"))
                .ToListAsync();

            if (plaintextKeys.Count > 0)
            {
                foreach (var pk in plaintextKeys)
                {
                    pk.Key = OperatorScopeGuard.HashCredential(pk.Key);
                }
                await db.SaveChangesAsync();
            }

            // Record applied schema version 3
            await db.Database.ExecuteSqlRawAsync(@"
                INSERT OR REPLACE INTO __SchemaVersions (VersionNumber, MigrationName, AppliedAtUtc)
                VALUES (3, '003_ScopedApiKeys_CommandLifecycle_UniqueTelemetry', datetime('now'));
            ");
        }
        finally
        {
            if (wasClosed)
            {
                await conn.CloseAsync();
            }
        }
    }

    private static async Task<HashSet<string>> GetExistingColumnsAsync(DbConnection conn, string tableName)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            string colName = reader.GetString(1);
            cols.Add(colName);
        }
        return cols;
    }

    private static async Task AddColumnIfMissingAsync(
        AppDbContext db,
        HashSet<string> existingColumns,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        if (!existingColumns.Contains(columnName))
        {
            string sql = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
            await db.Database.ExecuteSqlRawAsync(sql);
            existingColumns.Add(columnName);
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
