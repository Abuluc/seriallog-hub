# SerialLog Hub — Focused Security & Implementation Reconciliation Report

**Document Path:** `docs/audits/SECURITY_REMEDIATION_REPORT.md`  
**Verification Date:** 2026-11-22  
**Target Framework:** `.NET 10 LTS` (`net10.0` / `net10.0-windows`)

---

## 1. Verified Remediations Against Source Code (Issues 1 – 8)

Every one of the 8 confirmed issues has been verified directly against the current C# source code and tested via `dotnet test SerialLogHub.sln -c Release`:

| # | Confirmed Issue | Actual Source File & Line Reference | Verified Implementation in Source Code | Status |
| :--- | :--- | :--- | :--- | :--- |
| **1** | `OperatorScopeGuard` returning `true` when header is missing or `HttpContext` is null | `src/SerialLogHub.Api/Services/OperatorScopeGuard.cs` (Lines 140–262) | `AuthorizeOperatorAsync` fails closed (`401 Unauthorized`) whenever `httpContext == null`, when operator credentials (`X-OPERATOR-TOKEN` or `WebProxySigningSecret` HMAC) are missing, or when a bare `X-OPERATOR-EMAIL` header is sent without a verified credential. The legacy `IsAuthorizedForOperator` fail-open method has been completely removed. | **FIXED & VERIFIED** (`NegativeSecurity_NullHttpContext_FailsClosedWith401`, `NegativeSecurity_MissingOperatorHeaders_AreRejectedWith401`) |
| **2** | `X-OPERATOR-EMAIL` trusted as an identity source | `src/SerialLogHub.Api/Services/OperatorScopeGuard.cs` (Lines 182–259) | `X-OPERATOR-EMAIL` is **never** trusted as a standalone identity assertion. Operator identity is established exclusively from: (a) an indexed `O(1)` SQLite lookup of `X-OPERATOR-TOKEN` (`sha256:<hex>`) in `ApiKeys`, yielding the database-bound `keyEntity.OwnerEmail`, or (b) an `HMAC-SHA256` signature (`X-OPERATOR-PROOF`) signed with `ApiSecurity:WebProxySigningSecret` (which is strictly distinct from `ApiSecurity:ApiKey`). | **FIXED & VERIFIED** (`NegativeSecurity_ForgedBareOperatorEmailHeader_IsRejectedEvenWhenMatchingTarget`, `NegativeSecurity_OperatorAWithValidToken_CannotAccessOrIngestIntoOperatorB`) |
| **3** | `ApiKeyEntity` operator ownership field | `src/SerialLogHub.Api/Entities/ApiKeyEntity.cs` (Lines 10–32) | `ApiKeyEntity` defines `OwnerEmail` (`string.Empty` default; wildcard `"*"` values are prohibited and rejected with `403 Forbidden`), `ClientType` (`DesktopBridge` / `OperatorSession`), `IsRevoked`, and `ExpiresAt`. All secrets are stored as `sha256:<hex>` hashes. | **FIXED & VERIFIED** |
| **4** | `ApiKeyMiddleware` accepting API keys without establishing an authenticated operator identity | `src/SerialLogHub.Api/Middleware/ApiKeyMiddleware.cs` (Lines 53–179) | 1. Performs direct indexed `O(1)` SQL lookups (`FirstOrDefaultAsync(k => k.Key == providedHash)` — no `ToListAsync()`).<br/>2. Rejects any key with empty or `"*"` `OwnerEmail`.<br/>3. On all non-bootstrap protected routes (`/api/user/*`, `/api/telemetry/*`, `/api/auth/profile`, `/api/auth/updatename`), step 4 enforces that `AuthenticatedOperatorEmail` has been established on `HttpContext.Items` before calling `await _next(context)`, otherwise returning `401 Unauthorized`. | **FIXED & VERIFIED** (`NegativeSecurity_ApiKeyMiddleware_RejectsInvalidAndExpiredKeys_AndAcceptsHashedDbKey`) |
| **5** | `TelemetryController` loading historical timestamps into memory during ingestion | `src/SerialLogHub.Api/Controllers/TelemetryController.cs` (Lines 176–260) | Removed full-history and wide `[minTs, maxTs]` range queries. `IngestTelemetry` now queries only discrete 200-timestamp chunks (`WHERE DeviceId = @id AND RecordedAt IN (...)`) against the `UNIQUE (DeviceId, RecordedAt)` index (`IX_TelemetryRecords_DeviceId_RecordedAt`) and wraps batch insertion with `DbUpdateException` unique-constraint conflict handling. `CompareDevices` uses SQL-side `GroupBy` aggregation. | **FIXED & VERIFIED** (`TelemetryPerformance_HandlesLargeBatchDeduplication_AndRejectsOversizedBatch`) |
| **6** | `ProductionSecurityValidator` called at API startup | `src/SerialLogHub.Api/Program.cs` (Line 110) & `src/SerialLogHub.Api/Services/ProductionSecurityValidator.cs` | `ProductionSecurityValidator.ValidateApiConfiguration(app.Configuration, app.Environment.IsProduction())` is invoked directly in `Program.cs` immediately after `builder.Build()`. Rejects `RequireApiKey = false`, keys `< 16` chars, `WebProxySigningSecret == ApiKey`, and development fallback secrets (`dev-serialloghub-api-key`, `CHANGE_ME_IN_PRODUCTION`, `Demo1234!`). | **FIXED & VERIFIED** (`NegativeSecurity_ProductionSecurityValidator_*`) |
| **7** | `DbSeeder` using `EnsureCreatedAsync()` vs. EF Core Migrations | `src/SerialLogHub.Api/Data/DbSeeder.cs` (Lines 28–160), `src/SerialLogHub.Api/Migrations/20261122000000_InitialSerialLogHubSchema.cs`, `AppDbContextModelSnapshot.cs` | Removed `EnsureCreatedAsync()` from `DbSeeder`. Added EF Core 10 Migrations (`20261122000000_InitialSerialLogHubSchema` + `AppDbContextModelSnapshot`) and switched `DbSeeder` to `await db.Database.MigrateAsync()` (with non-destructive `__EFMigrationsHistory` baselining for pre-existing legacy SQLite files and post-migration `VerifyRequiredSchemaAsync`). | **FIXED & VERIFIED** (`DatabaseMigration_UpgradesLegacyV1SqliteDatabase_AndFailsWhenCoreTableMissing`) |
| **8** | Documentation vs. Source Code Alignment | `README.md`, `README.en.md`, `README.tr.md`, `docs/architecture/ARCHITECTURE.md`, `SECURITY.md`, `docs/demo/DEMO_GUIDE.md` | Reconciled all documentation diagrams, badges, table counts (7 domain tables + `__EFMigrationsHistory`), authentication flows (`X-OPERATOR-TOKEN` + `WebProxySigningSecret`), and test counts (`62/62`) with the actual `.NET 10 LTS` source code. | **FIXED & VERIFIED** |

---

## 2. Actual Build & Test Command Output

Commands executed against the current working tree:

```text
> dotnet restore SerialLogHub.sln --nologo
  Geri yükleme için tüm projeler güncel.

> dotnet build SerialLogHub.sln -c Release --no-restore --nologo
  SerialLogHub.Desktop -> ...\src\SerialLogHub.Desktop\bin\Release\net10.0-windows\SerialLogHub.Desktop.dll
  SerialLogHub.Api -> ...\src\SerialLogHub.Api\bin\Release\net10.0\SerialLogHub.Api.dll
  SerialLogHub.Web -> ...\src\SerialLogHub.Web\bin\Release\net10.0\SerialLogHub.Web.dll
  SerialLogHub.Tests -> ...\tests\SerialLogHub.Tests\bin\Release\net10.0-windows\SerialLogHub.Tests.dll
  Oluşturma başarılı oldu. (0 Hata)

> dotnet test SerialLogHub.sln -c Release --no-build --nologo
  Başarılı! - Başarısız: 0, Başarılı: 62, Atlanan: 0, Toplam: 62, Süre: 2 s - SerialLogHub.Tests.dll (net10.0)
```

---

## 3. Exact Modified & Created Files

- `src/SerialLogHub.Api/Entities/ApiKeyEntity.cs`
- `src/SerialLogHub.Api/Services/OperatorScopeGuard.cs`
- `src/SerialLogHub.Api/Services/ProductionSecurityValidator.cs`
- `src/SerialLogHub.Api/Middleware/ApiKeyMiddleware.cs`
- `src/SerialLogHub.Api/Data/AppDbContext.cs`
- `src/SerialLogHub.Api/Data/DbSeeder.cs`
- `src/SerialLogHub.Api/Migrations/20261122000000_InitialSerialLogHubSchema.cs`
- `src/SerialLogHub.Api/Migrations/AppDbContextModelSnapshot.cs`
- `src/SerialLogHub.Api/Controllers/AuthController.cs`
- `src/SerialLogHub.Api/Controllers/UserController.cs`
- `src/SerialLogHub.Api/Controllers/TelemetryController.cs`
- `src/SerialLogHub.Api/Program.cs`
- `src/SerialLogHub.Api/appsettings.json`
- `src/SerialLogHub.Api/appsettings.Development.json`
- `src/SerialLogHub.Web/Program.cs`
- `src/SerialLogHub.Web/Controllers/AuthController.cs`
- `src/SerialLogHub.Web/Services/ApiClientService.cs`
- `src/SerialLogHub.Web/appsettings.json`
- `src/SerialLogHub.Web/appsettings.Development.json`
- `src/SerialLogHub.Desktop/Program.cs`
- `src/SerialLogHub.Desktop/Services/BridgeApiClient.cs`
- `src/SerialLogHub.Desktop/Services/BridgeEngine.cs`
- `tests/SerialLogHub.Tests/UserControllerTests.cs`
- `tests/SerialLogHub.Tests/WebLayerTests.cs`
- `tests/SerialLogHub.Tests/TelemetryAndSecurityTests.cs`
- `docs/audits/SECURITY_REMEDIATION_REPORT.md`

---

## 4. Remaining Risks & Limitations

1. **Upstream Transitive NuGet Advisory (`NU1903` / `GHSA-2m69-gcr7-jv3q`):** `Microsoft.EntityFrameworkCore.Sqlite 10.0.5` transitively pulls `SQLitePCLRaw.lib.e_sqlite3 2.1.11`. All queries in SerialLog Hub are parameterized via EF Core LINQ with strict `5 MB` request body limits; upgrade `Microsoft.EntityFrameworkCore.Sqlite` when Microsoft bumps its `SQLitePCLRaw` dependency to `3.x`.
2. **Physical RS-232/USB Hardware Validation:** Serial protocol parsing, WMI port discovery, timeout cancellation callbacks, and bounded durable response buffering are verified via `SerialTransportTests` and `SimulatedSerialTransport`, whereas physical electrical cable disconnect mid-`WLB` flash read requires validation with a physical DataLogger unit.
