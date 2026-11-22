# Changelog

All notable changes to **SerialLog Hub** are documented in this file.

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
