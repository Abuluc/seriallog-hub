# Final Engineering Report — SerialLog Hub (.NET 10 LTS Upgrade & Security/Reliability Hardening)

**Repository:** `https://github.com/Abuluc/seriallog-hub`  
**Backup Checkpoint Branch:** `backup/pre-engineering-upgrade` (`2f5fe0d`)  
**Date:** 2026-11-22

---

## 1. Original Architecture

SerialLog Hub is a three-tier industrial telemetry and RS-232/USB DataLogger management platform consisting of:
- **`SerialLogHub.Api`:** ASP.NET Core Web API with SQLite + EF Core managing users, COM ports, and a polling command queue.
- **`SerialLogHub.Web`:** ASP.NET Core MVC + SignalR web dashboard with Cookie Authentication, a 6-step Device Configuration Wizard, uPlot + Chart.js visualizations, and IndexedDB local dataset storage.
- **`SerialLogHub.Desktop`:** Windows Forms + headless COM Port Bridge communicating with physical DataLoggers via `System.IO.Ports.SerialPort` (and WMI `Win32_SerialPort`) plus an isolated virtual simulator (`COM99`).
- **`SerialLogHub.Tests`:** xUnit test project.

---

## 2. Identified Problems (Phase Zero Audit)

1. **P0 — Cross-User Access (BOLA/IDOR) & Non-Constant-Time Secret Checks:** API endpoints relied solely on a shared `X-API-KEY` without validating that the caller's operator scope (`X-OPERATOR-EMAIL`) matched the requested `email` resource parameter; API/Bridge keys used `string.Equals`.
2. **P1 — Ephemeral Client-Only Telemetry Storage:** Telemetry dumps (`WLB`) were stored only in browser-local `IndexedDB` or raw command strings; no server-side relational `Devices` or `TelemetryRecords` tables existed for historical queries or device comparison.
3. **P1 — Simple Boolean Command State & Fixed Polling:** `CommandEntity` used only `bool IsProcessed` rather than an explicit lifecycle state machine (`Pending`, `Dispatched`, `Completed`, `Failed`, `TimedOut`), and `BridgeEngine` lacked adaptive idle backoff and non-idempotent hardware command guards.
4. **P1 — Basic Threshold-Only Anomaly Detection:** Client-side anomaly detection checked only static thresholds and 1-step deltas, without statistical Rolling Z-Score (Moving Average + Standard Deviation), Monotonic Sensor Drift, Missing Data Gap, Invalid Reading classification, or human-readable explanations.
5. **P2 — .NET 8 Target Framework & Missing Brute-Force / Reset Confirm Protections:** Projects targeted `.NET 8.0` instead of `.NET 10 LTS`, login endpoints lacked a strict per-IP brute-force rate limiter, password reset lacked a token-verified completion endpoint, and Dockerfiles ran as `root` without `HEALTHCHECK`.

---

## 3. Implemented Improvements

1. **Phase One (.NET 10 LTS Migration):**
   - Migrated `SerialLogHub.Api` and `SerialLogHub.Web` to `net10.0`, and `SerialLogHub.Desktop` and `SerialLogHub.Tests` to `net10.0-windows`.
   - Upgraded `Microsoft.EntityFrameworkCore.Sqlite` (`10.0.5`), `System.IO.Ports` (`10.0.0`), `System.Management` (`10.0.0`), `Dockerfile.api`, `Dockerfile.web`, and `.github/workflows/ci.yml` to `.NET 10.0`.
2. **Phase Two (Security Hardening):**
   - Implemented `OperatorScopeGuard` with `CryptographicOperations.FixedTimeEquals` and `X-OPERATOR-EMAIL` BOLA/IDOR enforcement across `UserController`, `TelemetryController`, and `AuthController` (returning `403 Forbidden` on cross-user access attempts).
   - Added `"auth-brute-force"` rate limiting (`10 req/min` per IP), HTTP security headers (`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy`, `X-Permitted-Cross-Domain-Policies`), `5 MB` Kestrel request body limit, and `POST /api/auth/confirm-reset`.
3. **Phase Three (Serial COM Bridge Reliability):**
   - Extended `CommandEntity` with `Status` (`CommandLifecycleStatus`), `DispatchedAt`, `CompletedAt`, `ErrorCategory`, and `AttemptCount`.
   - Added adaptive polling backoff and exponential error backoff to `BridgeEngine`, bounded retry backoff on `BridgeApiClient.UpdateCommandResponseAsync`, and `IsNonIdempotentPhysicalCommand` protection (`WLE1`, `WLH1`, `WLS0`, `WLM`, `WLA`, `WLV1`, `WLG`).
4. **Phase Four (Persistent Server-Side Telemetry & Device Storage):**
   - Added `DeviceEntity` (`Devices`) and `TelemetryRecordEntity` (`TelemetryRecords`) to `AppDbContext` with composite indexes (`(UserEmail, RecordedAt)`, `(DeviceId, RecordedAt)`), non-destructive SQLite schema upgrades in `DbSeeder`, automatic `WLB` dump persistence in `UserController.UpdateCommand`, and `TelemetryController` (`/api/telemetry/devices`, `/ingest`, `/history`, `/compare`, `/analyze`).
5. **Phase Five (Deterministic Statistical Anomaly Detection Engine):**
   - Built `AnomalyDetectionEngine` (C#) and upgraded `wwwroot/js/dashboard/storage.js` (JS) supporting 6 deterministic methods: `InvalidReading`, `StaticThreshold`, `SuddenSpike`, `RollingZScore` (Moving Average + StdDev with $\sigma \ge 0.25$ false-positive guard), `SensorDrift`, and `MissingDataGap`, with explainable tooltips.
6. **Phase Six (SignalR User & Device Groups):**
   - Upgraded `NotificationHub` to enlist authenticated connections into `user:{email}` and `device:{email}:{portName}` groups while maintaining multi-tab `UserConnectionManager` tracking.
7. **Phase Seven, Eight & Nine (Testing, CI/CD, Docker & Frontend UX):**
   - Expanded automated xUnit test suite from **22** to **45 passing tests** covering statistical anomaly methods, malformed simulator output, persistent telemetry deduplication, device comparison, command lifecycle transitions, password reset token security, and BOLA/IDOR cross-user blocking.
   - Added **Persistent Server Telemetry Archive & Multi-Device Comparison** panel to the Web Dashboard (`Views/Home/Index.cshtml`).
   - Hardened `Dockerfile.api` and `Dockerfile.web` with non-root `USER $APP_UID` and `--healthcheck` probes.

---

## 4. Files Modified & Created

- **Modified:**
  - `src/SerialLogHub.Api/SerialLogHub.Api.csproj`
  - `src/SerialLogHub.Api/Program.cs`
  - `src/SerialLogHub.Api/Data/AppDbContext.cs`
  - `src/SerialLogHub.Api/Data/DbSeeder.cs`
  - `src/SerialLogHub.Api/Entities/CommandEntity.cs`
  - `src/SerialLogHub.Api/Models/AuthDtos.cs`
  - `src/SerialLogHub.Api/Models/CommandDtos.cs`
  - `src/SerialLogHub.Api/Middleware/ApiKeyMiddleware.cs`
  - `src/SerialLogHub.Api/Controllers/AuthController.cs`
  - `src/SerialLogHub.Api/Controllers/UserController.cs`
  - `src/SerialLogHub.Desktop/SerialLogHub.Desktop.csproj`
  - `src/SerialLogHub.Desktop/Services/BridgeApiClient.cs`
  - `src/SerialLogHub.Desktop/Services/BridgeEngine.cs`
  - `src/SerialLogHub.Web/SerialLogHub.Web.csproj`
  - `src/SerialLogHub.Web/Program.cs`
  - `src/SerialLogHub.Web/Hubs/NotificationHub.cs`
  - `src/SerialLogHub.Web/Services/ApiClientService.cs`
  - `src/SerialLogHub.Web/Controllers/HomeController.cs`
  - `src/SerialLogHub.Web/Views/Home/Index.cshtml`
  - `src/SerialLogHub.Web/wwwroot/js/dashboard/storage.js`
  - `src/SerialLogHub.Web/wwwroot/js/dashboard/app.js`
  - `tests/SerialLogHub.Tests/SerialLogHub.Tests.csproj`
  - `deployment/Dockerfile.api`
  - `deployment/Dockerfile.web`
  - `.github/workflows/ci.yml`
- **Created:**
  - `src/SerialLogHub.Api/Entities/DeviceEntity.cs`
  - `src/SerialLogHub.Api/Entities/TelemetryRecordEntity.cs`
  - `src/SerialLogHub.Api/Services/OperatorScopeGuard.cs`
  - `src/SerialLogHub.Api/Services/AnomalyDetectionEngine.cs`
  - `src/SerialLogHub.Api/Controllers/TelemetryController.cs`
  - `tests/SerialLogHub.Tests/AnomalyDetectionEngineTests.cs`
  - `tests/SerialLogHub.Tests/TelemetryAndSecurityTests.cs`
  - `docs/CURRENT_ARCHITECTURE_REVIEW.md`
  - `docs/SECURITY_REVIEW.md`
  - `docs/COM_BRIDGE_RELIABILITY.md`
  - `docs/TELEMETRY_STORAGE.md`
  - `docs/ANOMALY_DETECTION.md`
  - `docs/SIGNALR_ARCHITECTURE.md`
  - `docs/ENGINEERING_UPGRADE_PROGRESS.md`
  - `docs/FINAL_ENGINEERING_REPORT.md`

---

## 5. Database & API Changes (Backward Compatible)

- **Database Changes (Non-Destructive):**
  - Added `Devices` and `TelemetryRecords` tables (`CREATE TABLE IF NOT EXISTS` in `DbSeeder`).
  - Added `Status`, `DispatchedAt`, `CompletedAt`, `ErrorCategory`, and `AttemptCount` columns to `Commands` (`ALTER TABLE ADD COLUMN` with defaults).
- **New API Endpoints:**
  - `POST /api/auth/confirm-reset`
  - `GET /api/telemetry/devices?email={email}`
  - `POST /api/telemetry/ingest`
  - `GET /api/telemetry/history?email={email}&deviceId={id}&fromUtc={iso}&toUtc={iso}&anomaliesOnly={bool}`
  - `GET /api/telemetry/compare?email={email}&deviceA={id1}&deviceB={id2}`
  - `POST /api/telemetry/analyze`
- **Breaking Changes:** **None.** All existing `/api/auth/*` and `/api/user/*` endpoints remain backward-compatible.

---

## 6. Final Summary & Verification Metrics

- **Completed Tasks:** Phases 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 (100% of phases completed).
- **Partially Completed / Uncompleted Tasks:** None.
- **Build Status:** `dotnet build SerialLogHub.sln --configuration Release` $\rightarrow$ **Succeeded (0 Errors)**.
- **Test Results:** `dotnet test SerialLogHub.sln --configuration Release --no-build` $\rightarrow$ **52 Passed, 0 Failed, 0 Skipped** (`~2s`). See [docs/FINAL_FOLLOWUP_AUDIT.md](FINAL_FOLLOWUP_AUDIT.md) for the follow-up negative security, migration, and performance verification details.
- **Migration Instructions:**
  - Ensure **.NET 10 SDK (`10.0.x`)** is installed locally (`dotnet --version`).
  - Existing SQLite databases (`serialloghub.db`) are upgraded automatically and non-destructively at startup by `DbSeeder.EnsureSchemaUpgradedAsync`.
