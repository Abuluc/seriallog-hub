# Phase Zero — Complete Repository & Architecture Audit

**Repository:** `https://github.com/Abuluc/seriallog-hub`  
**Audit Date:** 2026-11-22  
**Backup Checkpoint Branch:** `backup/pre-engineering-upgrade` (`2f5fe0d`)

---

## 1. Current Architecture & Component Responsibilities

SerialLog Hub is a three-tier industrial telemetry and RS-232/USB DataLogger management platform:

1. **`SerialLogHub.Api` (ASP.NET Core Web API, Port 8080):**
   - Central REST service backed by SQLite + EF Core (`AppDbContext`).
   - Manages operator authentication (`AuthController`), COM port registration, and the command relay queue (`UserController`).
   - Includes `PasswordSecurityService` (ASP.NET Core Identity `PasswordHasher<string>` PBKDF2 with legacy SHA-256 normalization), `ApiKeyMiddleware` (`X-API-KEY`), and `CloudDemoResponderService` (for zero-hardware cloud demos).
2. **`SerialLogHub.Web` (ASP.NET Core MVC + SignalR, Port 3000):**
   - Operator web portal with Cookie Authentication (`CookieAuthenticationDefaults`), `NotificationHub` (`/notificationHub`), 6-step Guided Device Configuration Wizard (`/Settings`), and 4-chart telemetry analytics dashboard (`uPlot` + `Chart.js`).
   - Receives real-time progress and telemetry dumps from the Desktop Bridge via `POST /Home/AppCheck` and `POST /Home/waitresponse` (`X-BRIDGE-KEY`).
3. **`SerialLogHub.Desktop` (.NET Windows Forms + Headless Mode):**
   - Runs on the operator's physical Windows machine to bridge local RS-232/USB COM ports (`RealSerialTransport` via `System.IO.Ports.SerialPort` + WMI `Win32_SerialPort`) and an isolated virtual simulator (`SimulatedSerialTransport` on `COM99`) to the cloud/web platform via `BridgeEngine` and `BridgeApiClient`.
4. **`SerialLogHub.Tests` (xUnit):**
   - Unit and controller tests covering `PasswordSecurityService`, `RealSerialTransport`, `SimulatedSerialTransport`, `UserController`, `UserConnectionManager`, `SettingsController`, and `HomeController`.

---

## 2. Current Data, Auth, Device & SignalR Flows

- **Authentication Flow:**
  - Web users submit credentials to `POST /api/auth/login` on `SerialLogHub.Web`, which forwards them to `SerialLogHub.Api` (`POST /api/auth/login`).
  - `PasswordSecurityService` normalizes raw or SHA-256 input and verifies against salted PBKDF2 (`PasswordHasher<string>`).
  - On success, `SerialLogHub.Web` issues an `HttpOnly`, `SameSite=Lax` authentication cookie containing `ClaimTypes.Name` and `ClaimTypes.Email`.
- **Device Communication Flow:**
  - `SerialLogHub.Desktop` enumerates COM ports and publishes them via `POST /api/user/updateports`.
  - When an operator sends a command (`TEST`, `WLB`, `WLJ`, `WLW`, `WLG`, `WLH0`, `WLH1`, `WLS0`, `WLE1`, `WLA`, `WLV1`, `WLM`), `SerialLogHub.Web` posts to `POST /api/user/SendCommand` and polls `GET /api/user/getresponses`.
  - `SerialLogHub.Desktop` polls `GET /api/user/getcommands`, executes the command against `RealSerialTransport` or `SimulatedSerialTransport`, streams `WLB` progress to `POST /Home/waitresponse`, and writes the final response via `POST /api/user/UpdateCommand`.
- **SignalR Flow:**
  - Browser connects to `/notificationHub` (`[Authorize]`).
  - `NotificationHub` registers `(email, connectionId)` in `UserConnectionManager`.
  - Desktop webhooks trigger `_hubContext.Clients.Clients(connectionIds).SendAsync("ReceiveMessage" | "DataReading", ...)`.
- **Database Structure (`AppDbContext`):**
  - Currently contains 5 tables: `Users`, `UsersPort`, `Commands`, `CommandHistories`, and `ApiKeys`.

---

## 3. Prioritized Findings (P0 – P3)

### P0 — Critical Findings

#### Finding P0-1: Missing Cryptographic Operator Scope & Timing-Safe Comparison on API Requests
- **File Path:** `src/SerialLogHub.Api/Middleware/ApiKeyMiddleware.cs` (`InvokeAsync`), `src/SerialLogHub.Api/Controllers/UserController.cs`
- **Actual Problem:**
  1. `ApiKeyMiddleware` compares `X-API-KEY` using standard `string.Equals` instead of constant-time `CryptographicOperations.FixedTimeEquals`.
  2. Any caller possessing the shared `X-API-KEY` can query or mutate any user's ports, commands, or history by passing an arbitrary `email` query/body parameter without proving operator ownership (`X-OPERATOR-EMAIL` scope header enforcement).
- **Why It Matters:** Allows horizontal privilege escalation (BOLA/IDOR) if a bridge client or API consumer requests another user's email, and exposes API key comparison to timing side-channels.
- **Recommended Solution:**
  - Use `CryptographicOperations.FixedTimeEquals` for both `X-API-KEY` and `X-BRIDGE-KEY` checks.
  - Enforce operator scope in `SerialLogHub.Api`: when `X-OPERATOR-EMAIL` is supplied (or bound to a scoped API key in `ApiKeys`), validate that the target `email` in `UserController` and `TelemetryController` matches the authenticated operator scope, rejecting cross-user access with `403 Forbidden`.
- **Implementation Status:** Planned (Phase Two).

---

### P1 — High Findings

#### Finding P1-1: Lack of Server-Side Persistent Telemetry & Device Storage
- **File Path:** `src/SerialLogHub.Api/Data/AppDbContext.cs`, `src/SerialLogHub.Web/wwwroot/js/dashboard/storage.js`
- **Actual Problem:** Telemetry datasets (`WLB` serial dumps and uploaded CSVs) are stored only in browser-local `IndexedDB` (`serialLogHubDB`) or as raw unindexed strings inside `CommandHistories.Response`. There are no relational `Devices` or `TelemetryRecords` tables in SQLite.
- **Why It Matters:** Operators lose historical telemetry when switching browsers/machines, and the backend cannot perform indexed date-range queries, multi-device comparisons, or persistent anomaly auditing.
- **Recommended Solution:** Add `DeviceEntity` (`Devices`) and `TelemetryRecordEntity` (`TelemetryRecords`) to `AppDbContext` with foreign keys, composite indexes (`(DeviceId, RecordedAt)`), ownership enforcement, automatic ingestion of `WLB` serial dumps, and REST endpoints (`GET/POST /api/telemetry/...`) for historical querying, device comparison, and CSV/Excel export.
- **Implementation Status:** Planned (Phase Four).

#### Finding P1-2: Boolean Command State Instead of Explicit Lifecycle State Machine & Adaptive Polling
- **File Path:** `src/SerialLogHub.Api/Entities/CommandEntity.cs`, `src/SerialLogHub.Api/Controllers/UserController.cs`, `src/SerialLogHub.Desktop/Services/BridgeEngine.cs`
- **Actual Problem:**
  1. `CommandEntity` tracks state only via `bool IsProcessed` and `string Response`, lacking explicit lifecycle states (`Pending`, `Dispatched`, `Executing`, `Completed`, `Failed`, `TimedOut`, `Cancelled`), `DispatchedAt`, `CompletedAt`, and error classification.
  2. Once `GET /api/user/getcommands` returns a pending command, it remains `IsProcessed = false` without marking `Dispatched`, so overlapping polls could re-fetch the same command.
  3. `BridgeEngine` polls at a static interval without exponential backoff during inactivity or transient network failures, and `RealSerialTransport` does not classify hardware errors vs. timeouts vs. non-idempotent commands (`WLE1`).
- **Why It Matters:** Can cause duplicate command dispatch or unnecessary network load during idle periods.
- **Recommended Solution:** Upgrade `CommandEntity` with `Status` (`CommandLifecycleStatus`), `DispatchedAt`, `CompletedAt`, and `ErrorCategory`; transition commands atomically (`Pending -> Dispatched -> Executing -> Completed/Failed/TimedOut`); add adaptive polling backoff in `BridgeEngine` and safe non-idempotent command protection (`WLE1` never auto-retried).
- **Implementation Status:** Planned (Phase Three).

#### Finding P1-3: Basic Client-Only Anomaly Detection Without Statistical / Explainable Engine
- **File Path:** `src/SerialLogHub.Web/wwwroot/js/dashboard/storage.js` (`detectAnomalies`)
- **Actual Problem:** Anomaly detection only checks `temperature > maxTemp`, `humidity < 0 || humidity > 100`, and a 1-step rate delta on the client. There is no server-side or shared deterministic statistical engine supporting Moving Average, Rolling Standard Deviation / Z-Score, Sensor Drift, Missing Data gaps, or structured explainable anomaly records (`Method`, `MeasuredValue`, `ExpectedRange`, `Severity`, `Timestamp`, `Explanation`).
- **Why It Matters:** Gradual sensor drift, statistical outliers within static thresholds, and missing-data dropouts go undetected, and anomalies lack human-readable explanations.
- **Recommended Solution:** Implement `AnomalyDetectionEngine` in `SerialLogHub.Api.Services` (and expose it to Web & tests) supporting Threshold, Sudden Spike, Rolling Z-Score (Moving Average + StdDev), Sensor Drift, Missing Data, and Invalid Reading detection with explainable results.
- **Implementation Status:** Planned (Phase Five).

---

### P2 — Medium Findings

#### Finding P2-1: Solution Targets .NET 8 Instead of Installed .NET 10 LTS
- **File Path:** `src/SerialLogHub.Api/SerialLogHub.Api.csproj`, `src/SerialLogHub.Web/SerialLogHub.Web.csproj`, `src/SerialLogHub.Desktop/SerialLogHub.Desktop.csproj`, `tests/SerialLogHub.Tests/SerialLogHub.Tests.csproj`, `deployment/Dockerfile.*`, `.github/workflows/ci.yml`
- **Actual Problem:** All projects target `net8.0` / `net8.0-windows`, while `.NET 10 SDK (10.0.404)` is installed and requested for LTS migration.
- **Why It Matters:** Keeping the solution on .NET 10 aligns with the requested LTS target while preserving full compatibility.
- **Recommended Solution:** Migrate `TargetFramework` to `net10.0` / `net10.0-windows`, update Dockerfiles and CI workflow to `.NET 10.0.x`, verify NuGet package compatibility, and confirm all builds and tests pass.
- **Implementation Status:** Planned (Phase One).

#### Finding P2-2: Auth Brute-Force Rate Limiting, Security Headers & Password Reset Token Verification
- **File Path:** `src/SerialLogHub.Api/Program.cs`, `src/SerialLogHub.Web/Program.cs`, `src/SerialLogHub.Api/Controllers/AuthController.cs`
- **Actual Problem:**
  1. Both API and Web only apply a global 180 req/min rate limiter; authentication endpoints (`/api/auth/login`) lack a strict per-IP brute-force policy.
  2. Security headers (`X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy`, `X-Permitted-Cross-Domain-Policies`, `Content-Security-Policy`) are not emitted by default.
  3. `AuthController` generates `ResetPasswordToken` and `TokenExpiryTime`, but lacks a `POST /api/auth/confirm-reset` endpoint to verify token expiration and complete password rotation securely.
- **Why It Matters:** Hardens login against credential stuffing and clickjacking/MIME-sniffing attacks.
- **Recommended Solution:** Add a strict `"auth-policy"` rate limiter, security headers middleware, and token-validated password reset completion.
- **Implementation Status:** Planned (Phase Two).

#### Finding P2-3: SignalR In-Memory Dictionary Without Native Hub Groups & Duplicate Protection
- **File Path:** `src/SerialLogHub.Web/Hubs/NotificationHub.cs`, `src/SerialLogHub.Web/Controllers/HomeController.cs`
- **Actual Problem:** SignalR broadcasts rely exclusively on `UserConnectionManager.GetConnectionIds(email)` instead of SignalR groups (`user:{email}` and `device:{email}:{port}`), and webhook payloads lack sequence/event IDs to prevent duplicate message rendering on reconnect.
- **Why It Matters:** Native SignalR groups are required for clean multi-tab/device subscription isolation and future backplane scaling.
- **Recommended Solution:** Enlist authenticated connections into `user:{email}` groups in `NotificationHub`, support device group subscriptions (`JoinDeviceGroup`), and include event IDs/timestamps in SignalR payloads.
- **Implementation Status:** Planned (Phase Six).

---

### P3 — Low Findings

#### Finding P3-1: Docker Non-Root Execution, Healthchecks & CI Pipeline Coverage/Docker Verification
- **File Path:** `deployment/Dockerfile.api`, `deployment/Dockerfile.web`, `.github/workflows/ci.yml`
- **Actual Problem:** Dockerfiles run as `root` without `HEALTHCHECK` directives, and `.github/workflows/ci.yml` does not collect code coverage or run `dotnet list package --vulnerable`.
- **Why It Matters:** Container hardening and automated vulnerability/coverage checks are standard DevSecOps gates.
- **Recommended Solution:** Add non-root user and `HEALTHCHECK` to Dockerfiles, and add coverage collection + dependency vulnerability checks to `.github/workflows/ci.yml`.
- **Implementation Status:** Planned (Phase Eight).
