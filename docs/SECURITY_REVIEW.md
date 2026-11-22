# Phase Two — Application & DevSecOps Security Review

**Project:** SerialLog Hub (.NET 10 LTS)  
**Review Date:** 2026-11-22

---

## 1. Discovered Vulnerabilities & Implemented Remediations

| ID | Category | Severity | Affected Component | Vulnerability Description | Remediation Implemented | Status |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **SEC-01** | BOLA / IDOR (Cross-User Access) | **P0 (Critical)** | `UserController`, `TelemetryController`, `AuthController` | Callers holding the shared `X-API-KEY` could read or mutate another operator's COM ports, command queue, audit history, or telemetry by passing a different `email` parameter. | Added `OperatorScopeGuard.IsAuthorizedForOperator(HttpContext, targetEmail)` enforcing `X-OPERATOR-EMAIL` header scope across `UserController`, `TelemetryController`, and `AuthController`. Mismatched operator scopes are rejected with `403 Forbidden`. Both `ApiClientService` (Web) and `BridgeApiClient` (Desktop) attach `X-OPERATOR-EMAIL` on all user-scoped requests. | **Fixed & Verified** (`BOLA_OperatorScopeGuard_BlocksCrossUserAccessAcrossControllers`) |
| **SEC-02** | Timing Side-Channel on Secret Comparison | **P1 (High)** | `ApiKeyMiddleware`, `HomeController.IsBridgeAuthorized`, `AuthController.ConfirmPasswordReset` | `X-API-KEY`, `X-BRIDGE-KEY`, and password reset tokens were compared using `string.Equals` instead of constant-time comparison. | Replaced secret comparisons with `CryptographicOperations.FixedTimeEquals` via `OperatorScopeGuard.FixedTimeEquals`. | **Fixed & Verified** |
| **SEC-03** | Login & Password Reset Brute-Force Protection | **P1 (High)** | `SerialLogHub.Api/Program.cs`, `AuthController` | Only a global 180 req/min rate limit existed; authentication endpoints lacked a dedicated per-IP brute-force limit. | Added the `"auth-brute-force"` fixed-window policy (`10` permits/minute per IP, `QueueLimit = 0`, `429 Too Many Requests`) and applied `[EnableRateLimiting("auth-brute-force")]` to `Login`, `Register`, `SendResetMail`, `ConfirmPasswordReset`, and `ResendVerificationEmail`. | **Fixed & Verified** |
| **SEC-04** | Password Reset Token Expiration & Replay | **P1 (High)** | `AuthController` | Password reset tokens were generated (`SendResetMail`) without a token-verified completion endpoint that enforced `TokenExpiryTime > DateTime.UtcNow` and single-use invalidation. | Implemented `POST /api/auth/confirm-reset` (`ConfirmPasswordReset`) enforcing expiration, constant-time token verification, salted PBKDF2 hashing (`PasswordSecurityService.HashForStorage`), and immediate token invalidation (`ResetPasswordToken = "None"`). | **Fixed & Verified** (`AuthController_ConfirmPasswordReset_EnforcesTokenExpirationAndFixedTimeMatch`) |
| **SEC-05** | HTTP Security Headers & Request Size Limits | **P2 (Medium)** | `SerialLogHub.Api/Program.cs`, `SerialLogHub.Web/Program.cs` | Security headers (`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `X-Permitted-Cross-Domain-Policies`) and explicit Kestrel body size limits were not configured. | Added security headers middleware and `MaxRequestBodySize = 5 MB` in both `SerialLogHub.Api` and `SerialLogHub.Web`. | **Fixed & Verified** |
| **SEC-06** | Destructive Command CSRF / Method Hardening | **P2 (Medium)** | `SettingsController.Format` (`WLE1`) | Flash erase (`WLE1`) was previously reachable via `HTTP GET`. | Restricted `/Settings/Format` strictly to `[HttpPost]` (`GET` returns `405 Method Not Allowed`). | **Fixed & Verified** |
| **SEC-07** | Container Privilege Hardening | **P2 (Medium)** | `deployment/Dockerfile.api`, `deployment/Dockerfile.web` | Runtime containers executed as `root` without container health checks. | Updated Dockerfiles to `mcr.microsoft.com/dotnet/aspnet:10.0`, chowned directories to `$APP_UID`, switched to `USER $APP_UID`, and added native `--healthcheck` probes. | **Fixed & Verified** |

---

## 2. Authentication & Password Hashing Architecture

- **Server-Side Salted PBKDF2 (`PasswordSecurityService`):**
  - Uses `Microsoft.AspNetCore.Identity.PasswordHasher<string>` (PBKDF2 with HMAC-SHA256/SHA512, 128-bit random salt, 100,000+ iterations).
  - **Backward-Compatible Normalization (`NormalizeClientHash`):** Because legacy Desktop/Web clients pre-hashed passwords with SHA-256 while registration sent raw passwords, `NormalizeClientHash` canonicalizes both inputs before applying salted PBKDF2 on the server. A raw SHA-256 string alone is never stored in the database.

---

## 3. Remaining Known Upstream Risk

- **Upstream NuGet Advisory (`NU1903` / `GHSA-2m69-gcr7-jv3q` in `SQLitePCLRaw.lib.e_sqlite3 2.1.11`):**
  - `Microsoft.EntityFrameworkCore.Sqlite 10.0.5` transitively depends on `Microsoft.Data.Sqlite.Core 10.0.5`, which pins `SQLitePCLRaw.bundle_e_sqlite3 2.1.11`.
  - **Mitigation:** SerialLog Hub does not accept arbitrary user-supplied SQL queries; all database access uses parameterized EF Core LINQ queries and strict request size limits (`5 MB`). Upgrade `Microsoft.EntityFrameworkCore.Sqlite` when Microsoft releases the `SQLitePCLRaw 3.x` binding update.
