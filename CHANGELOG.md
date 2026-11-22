# Changelog

All notable changes to **SerialLog Hub** are documented in this file.

## [2.0.0] - 2026-11-22

### Added
- **.NET 10 LTS Migration:** Migrated `SerialLogHub.Api`, `SerialLogHub.Web`, `SerialLogHub.Desktop`, and `SerialLogHub.Tests` (plus Dockerfiles and GitHub Actions CI) to `.NET 10.0` (`net10.0` / `net10.0-windows`).
- **Persistent Server-Side Telemetry & Multi-Device Comparison (`TelemetryController`):** Added relational `Devices` and `TelemetryRecords` SQLite tables with composite indexes, automatic `WLB` dump persistence, paginated historical queries (`GET /api/telemetry/history`), multi-device side-by-side comparison (`GET /api/telemetry/compare`), and a **Persistent Server Telemetry Archive & Device Comparison** panel on the Web Dashboard.
- **Deterministic Statistical Anomaly Detection Engine (`AnomalyDetectionEngine`):** Implemented 6 explainable detection methods across C# and JavaScript: `InvalidReading`, `StaticThreshold`, `SuddenSpike`, `RollingZScore` (Moving Average + Standard Deviation), `SensorDrift`, and `MissingDataGap`.
- **Comprehensive 45-Test Quality & Security Suite:** Added `AnomalyDetectionEngineTests` and `TelemetryAndSecurityTests` verifying statistical outlier detection, malformed simulator output, BOLA/IDOR blocking, password reset token expiration, and command lifecycle transitions.
- **Architecture & Engineering Audit Documentation:** Added `CURRENT_ARCHITECTURE_REVIEW.md`, `SECURITY_REVIEW.md`, `COM_BRIDGE_RELIABILITY.md`, `TELEMETRY_STORAGE.md`, `ANOMALY_DETECTION.md`, `SIGNALR_ARCHITECTURE.md`, `ENGINEERING_UPGRADE_PROGRESS.md`, and `FINAL_ENGINEERING_REPORT.md` under `docs/`.

### Security & Reliability
- **BOLA / Cross-User Protection (`OperatorScopeGuard`):** Enforced `X-OPERATOR-EMAIL` scope validation across `UserController`, `TelemetryController`, and `AuthController` (`403 Forbidden` on cross-user access) and constant-time secret comparison (`CryptographicOperations.FixedTimeEquals`).
- **Auth Hardening:** Added `"auth-brute-force"` rate-limit policy (`10 req/min` per IP), `POST /api/auth/confirm-reset` token-validated password reset completion, security headers middleware, and non-root Docker execution (`USER $APP_UID`) with `--healthcheck` probes.
- **Explicit Command Lifecycle & Adaptive Bridge Polling:** Added `Status` (`Pending`, `Dispatched`, `Completed`, `Failed`, `TimedOut`), `DispatchedAt`, `CompletedAt`, `ErrorCategory`, adaptive idle/error polling backoff, and non-idempotent hardware command protection (`WLE1`, `WLH1`, `WLS0`, `WLM`, `WLA`, `WLV1`, `WLG`).

## [1.3.0] - 2026-11-22

### Improved
- **Solution & CI Pipeline Integrity:** Added `SerialLogHub.Tests` to `SerialLogHub.sln` and updated `.github/workflows/ci.yml` so `dotnet build SerialLogHub.sln` and `dotnet test SerialLogHub.sln --no-build` build and execute all tests cleanly in CI.
- **Expanded Automated Test Suite (22 xUnit Tests):** Added `UserControllerTests` (SQLite in-memory tests for 60s stale command expiration, `UpdateCommand` idempotency, `GetCommandHistory` ordering/error flags, and port sync) and `WebLayerTests` (multi-tab `UserConnectionManager`, `SettingsController.SetMM` threshold validation, and `HomeController.TestConnection` placeholder rejection).
- **Modular Dashboard Frontend:** Split the monolithic `dashboard.js` into focused modules under `wwwroot/js/dashboard/` (`storage.js`, `charts.js`, `app.js`).
- **Clean English Naming & Routes:** Replaced legacy `"Response yok"` sentinel with `"Pending"`, renamed SignalR event `datareadding` to `DataReading`, and added clean English route names (`/Home/GetAvailablePorts`, `/Home/DataTable`, `/Settings/SetComment`) while keeping legacy route aliases for backwards compatibility.
- **Refreshed Screenshots:** Updated `docs/screenshots/02-dashboard-telemetry.png` to showcase Device Diagnostics, Adjustable Anomaly Thresholds, and Command Audit Log.

## [1.2.0] - 2026-11-22

### Added
- **Command Audit Log (`GET /api/user/history` & Dashboard Panel):** Real-time command history table displaying recent port commands, status/error badges, response previews, and UTC timestamps alongside local IndexedDB datasets.
- **Adjustable Anomaly Thresholds:** Interactive dashboard controls (`Max Temp °C`, `Temp Rate °C/10s`, `Humidity Rate %/10s`) that recalculate anomaly flags, KPI counters, table rows, and all Chart.js analytics charts live without re-uploading CSV data.
- **One-Click Device Diagnostics:** Queries Device Label (`WLW`), Battery (`WLJ`), and RTC Clock (`WLH0`) sequentially and renders a compact diagnostics strip in the Serial Terminal panel.

### Fixed
- **Strict COM Port Handshake Validation:** Fixed false-positive `"Connected"` status when connecting to unrelated, missing, or non-DataLogger COM ports (e.g., Bluetooth serial links); `TestConnection` now requires a valid `CMD,OK,LCZ,` or `CMD,OK,LSS,` signature and clears stale `Connection` cookies on failure.
- **Desktop Bridge Reliability & UI Thread Safety:** Offloaded `SerialPort` I/O and WMI port enumeration off the WinForms UI thread, added `SemaphoreSlim` port-sync serialization, cancellation-aware `ReadLine()` timeouts, safe `async void` exception handling, and deduplication of completed commands.
- **API Command Queue Hygiene:** Auto-expires stale pending commands older than 60 seconds in `GET /api/user/getcommands` and guards `UpdateCommand` against duplicate history entries.
- **Web Dashboard & Wizard Hardening:** Fixed multi-tab SignalR tracking (`UserConnectionManager`), composable telemetry filters (tab + index search + time range), large-dataset min/max stack safety, `WLB` duplicate ingestion between SignalR and HTTP, `uPlot` instance cleanup, HTML escaping in history/audit tables, `[HttpPost]` enforcement on `/Settings/Format`, and server/client validation on `/Settings/SetMM`.

## [1.1.0] - 2026-11-22

### Added
- **Guided 6-Step Device Configuration Wizard (`/Settings`):** Replaced individual sidebar configuration links with a single sequential wizard supporting **Previous**, **Skip Step**, **Save & Continue**, non-clickable visual progress stepper (Completed / Skipped / Active), and a completion summary screen.
- **Multi-Chart Engineering Analytics Dashboard:** Added live **Min / Avg / Max** telemetry statistics strip plus 3 new interactive Chart.js visualizations alongside uPlot:
  - Temperature Distribution Histogram (Bar Chart)
  - Temperature vs. Humidity Correlation (Scatter Plot highlighting anomalies)
  - Health & Anomaly Breakdown (Doughnut Chart)
- **Updated Documentation & Screenshots:** Refreshed live screenshots and bilingual `README.en.md` / `README.tr.md` with the new analytics dashboard and guided configuration wizard.

## [1.0.0] - 2026-11-22

### Added
- **`SerialLogHub.Api` (.NET 8 Web API):** Rebuilt contract-compatible REST API with SQLite + EF Core 8, Swagger/OpenAPI, `X-API-KEY` middleware, salted PBKDF2 password hashing, fixed-window rate limiting, and optional `CloudDemoResponderService` with periodic demo reset.
- **`SerialLogHub.Web` (.NET 8 MVC + SignalR):** Modernized English-only telemetry dashboard with uPlot charting, combined rate-of-change + threshold anomaly detection, IndexedDB history, ExcelJS `.xlsx` export, 6-step Device Configuration Wizard, and Simulation/Demo Mode banner.
- **`SerialLogHub.Desktop` (.NET 8 Windows Forms):** Migrated COM Port Bridge from .NET Framework 4.7.2 to .NET 8 Windows with isolated `RealSerialTransport` (preserving hardware `WL*` serial protocol + WMI device names) and optional `SimulatedSerialTransport` (`COM99`).
- **`SerialLogHub.Tests` (xUnit):** Automated unit tests for password hashing, WMI port name parsing, and virtual DataLogger protocol responses.
- **Deployment & Documentation:** Dockerfiles, `docker-compose.yml`, `render.yaml`, bilingual `README.en.md` / `README.tr.md`, `ARCHITECTURE.md`, `API_DOCUMENTATION.md`, `SECURITY.md`, and live UI screenshots.

### Security
- Removed all legacy hardcoded SQL Server credentials, public IP addresses, JWT secrets, ClickOnce certificates, and direct client database connections.
- Secured SignalR `/notificationHub` with `[Authorize]` and desktop webhooks with `X-BRIDGE-KEY`.
- Replaced client-cookie identity lookups with signed `User.Identity.Name` claims.
