# SerialLog Hub — Critical Follow-Up Security & Quality Audit Report

**Repository:** `https://github.com/Abuluc/seriallog-hub`  
**Target Framework:** `.NET 10 LTS` (`net10.0` / `net10.0-windows`)  
**Audit & Verification Date:** 2026-11-22

---

## 1. Executive Summary

An independent follow-up security and quality audit was conducted against the `.NET 10 LTS` implementation of **SerialLog Hub**. All five critical follow-up areas—**Authoritative BOLA / Operator Identity Binding**, **Negative Security Testing**, **Versioned SQLite Schema Migration Reliability**, **Bounded-Memory Telemetry Ingestion Performance**, and **Documentation / Physical Hardware Validation Clarity**—were remediated and verified with **52/52 passing xUnit tests** in Release mode.

---

## 2. Detailed Findings & Remediations

### Finding F-01: Unverified `X-OPERATOR-EMAIL` Header & Missing Operator Identity Bypass (BOLA)
- **Severity:** **P0 (Critical)**
- **Affected Files:**
  - `src/SerialLogHub.Api/Services/OperatorScopeGuard.cs`
  - `src/SerialLogHub.Api/Middleware/ApiKeyMiddleware.cs`
  - `src/SerialLogHub.Api/Entities/ApiKeyEntity.cs`
  - `src/SerialLogHub.Api/Controllers/AuthController.cs`
  - `src/SerialLogHub.Api/Controllers/UserController.cs`
  - `src/SerialLogHub.Api/Controllers/TelemetryController.cs`
  - `src/SerialLogHub.Web/Services/ApiClientService.cs`
  - `src/SerialLogHub.Desktop/Services/BridgeApiClient.cs`
- **Root Cause:**
  1. `OperatorScopeGuard.IsAuthorizedForOperator` returned `true` when `X-OPERATOR-EMAIL` was absent.
  2. When `X-OPERATOR-EMAIL` was present, it only compared the client-supplied header string against the target email parameter without cryptographically binding the request to an authenticated operator session or signed proof. A consumer holding a shared `X-API-KEY` could impersonate another user by forging `X-OPERATOR-EMAIL`.
  3. `ApiKeys` stored plaintext secrets without `OwnerEmail`, `ClientType`, or `IsRevoked` metadata.
- **Implemented Fix:**
  1. **Extended `ApiKeyEntity`:** Added `OwnerEmail`, `ClientType` (`WebServer`, `DesktopBridge`, `OperatorSession`), and `IsRevoked`, and stored all secrets as `sha256:<hex>` hashes (`OperatorScopeGuard.HashCredential`).
  2. **Issued Operator-Bound Session Tokens at Login (`POST /api/auth/login`):** `AuthController.Login` generates a 256-bit cryptographically random session token (`slh_op_<hex>`), stores its SHA-256 hash in `ApiKeys` bound to `OwnerEmail = user.Email`, and returns `operatorToken` to the client (`BridgeApiClient` attaches it as `X-OPERATOR-TOKEN`).
  3. **Cryptographic WebServer Proxy Proof (`X-OPERATOR-PROOF`):** `ApiClientService` (Web) signs `(operatorEmail|unixTimestamp)` with `HMAC-SHA256` (`X-OPERATOR-TIMESTAMP` + `X-OPERATOR-PROOF`) enforced within a $\pm 300\text{s}$ replay window.
  4. **Authoritative Server-Side Enforcement (`OperatorScopeGuard.AuthorizeOperatorAsync`):**
     - Rejects missing operator identity headers on HTTP requests with `401 Unauthorized`.
     - Rejects bare/unverified `X-OPERATOR-EMAIL` headers (even when matching `targetEmail`) with `401 Unauthorized`.
     - Resolves the caller's true identity from the database-bound `OwnerEmail` of `X-OPERATOR-TOKEN` (or verified HMAC proof) and rejects any cross-user access or forged header conflict with `403 Forbidden`.
     - Rejects expired or revoked credentials with `401 Unauthorized`.
- **Tests Executed:**
  - `NegativeSecurity_MissingOperatorHeaders_AreRejectedWith401`
  - `NegativeSecurity_ForgedBareOperatorEmailHeader_IsRejectedEvenWhenMatchingTarget`
  - `NegativeSecurity_OperatorAWithValidToken_CannotAccessOperatorB_PortsCommandsOrTelemetry`
  - `NegativeSecurity_ExpiredRevokedAndReplayedCredentials_AreRejectedWith401`
  - `NegativeSecurity_ApiKeyMiddleware_RejectsInvalidAndExpiredKeys_AndAcceptsHashedDbKey`
  - `NegativeSecurity_WebBridgeWebhooks_RejectInvalidBridgeCredentials`
- **Actual Test Result:** **6/6 Passed (`52/52` total suite passed).**
- **Remaining Limitations:** None for single-API-secret + DB token topology; in multi-tenant enterprise deployments, asymmetric JWT/OIDC signing keys can replace shared Web-to-API HMAC secrets.

---

### Finding F-02: Ad-Hoc `ALTER TABLE` Schema Upgrades with Broad Exception Swallowing
- **Severity:** **P1 (High)**
- **Affected Files:**
  - `src/SerialLogHub.Api/Data/DbSeeder.cs`
- **Root Cause:**
  - `EnsureSchemaUpgradedAsync` previously executed raw `ALTER TABLE` statements inside an empty `try { ... } catch { }` loop and caught outer exceptions as non-fatal warnings, which could mask real schema corruption or migration errors.
- **Implemented Fix:**
  - Replaced `EnsureSchemaUpgradedAsync` with `DbSeeder.UpgradeSchemaAndMigrateAsync(AppDbContext db)`:
    - Creates a versioned `__SchemaVersions` tracking table (`VersionNumber`, `MigrationName`, `AppliedAtUtc`).
    - Queries `PRAGMA table_info(<TableName>)` via `GetExistingColumnsAsync` to inspect actual SQLite columns before executing `ALTER TABLE ADD COLUMN` (`AddColumnIfMissingAsync`) without swallowing exceptions.
    - Non-destructively hashes any legacy plaintext keys in `ApiKeys` (`sha256:<hex>`) while preserving all existing `Users`, `UsersPort`, `Commands`, `CommandHistories`, `Devices`, and `TelemetryRecords`.
- **Tests Executed:**
  - `DatabaseMigration_UpgradesLegacyV1SqliteDatabase_PreservingAllDataAndHashingKeys`
- **Actual Test Result:** **Passed** (verified against an in-memory legacy v1 5-table SQLite schema populated with pre-existing rows and executed twice for idempotency).
- **Remaining Limitations:** Uses deterministic SQLite `PRAGMA` introspection + `__SchemaVersions` rather than `dotnet-ef` CLI bundle files so zero-config startup works identically in Docker/Render and local environments.

---

### Finding F-03: Unbounded Memory Usage During Telemetry Ingestion Deduplication
- **Severity:** **P1 (High)**
- **Affected Files:**
  - `src/SerialLogHub.Api/Controllers/TelemetryController.cs`
  - `src/SerialLogHub.Api/Controllers/UserController.cs`
  - `src/SerialLogHub.Api/Data/AppDbContext.cs`
  - `src/SerialLogHub.Api/Data/DbSeeder.cs`
- **Root Cause:**
  - `TelemetryController.IngestTelemetry` loaded **all** historical `RecordedAt` timestamps for a device into a `HashSet<DateTime>` in application memory (`Where(r => r.DeviceId == device.Id).Select(r => r.RecordedAt).ToListAsync()`), and `IX_TelemetryRecords_DeviceId_RecordedAt` was a non-unique index.
- **Implemented Fix:**
  1. Upgraded `IX_TelemetryRecords_DeviceId_RecordedAt` on `TelemetryRecords (DeviceId, RecordedAt)` to a **database-level `UNIQUE` index** in both `AppDbContext` and `DbSeeder`.
  2. Enforced `MaxSamplesPerIngest = 5000` and `MaxRawPayloadCharacters = 2 MB` per ingestion request.
  3. Deduplicated incoming samples in-batch first (`GroupBy(s => s.RecordedAt)`), sorted chronologically, and queried SQLite **only** within the bounded `[minTs, maxTs]` range of the incoming batch (`Where(r => r.DeviceId == device.Id && r.RecordedAt >= minTs && r.RecordedAt <= maxTs)`), followed by batched `AddRange(toInsert)`.
- **Tests Executed:**
  - `TelemetryPerformance_HandlesLargeBatchDeduplication_AndRejectsOversizedBatch` (1,500-sample batch insert + 600-sample partial overlap deduplication + 5,001-sample rejection)
  - `TelemetryController_IngestHistoryAndCompare_PersistsAndDeduplicatesRecords`
- **Actual Test Result:** **Passed.**
- **Remaining Limitations:** None at current single-node SQLite scale.

---

### Finding F-04: Documentation Consistency & Historical Audit Separation
- **Severity:** **P2 (Medium)**
- **Affected Files:**
  - `PROJECT_AUDIT.md`, `README.en.md`, `README.tr.md`, `CHANGELOG.md`, `docs/SECURITY_REVIEW.md`, `docs/COM_BRIDGE_RELIABILITY.md`, `docs/FINAL_ENGINEERING_REPORT.md`
- **Root Cause:**
  - `PROJECT_AUDIT.md` described the legacy 3-folder internship code (`WhiloggerWEB-master`, `WhiloggerU--master`, `WebPortApp-main`) without a top banner marking it as a historical pre-refactor baseline, and test counts needed updating to `52/52`.
- **Implemented Fix:**
  - Added a prominent `HISTORICAL PRE-REFACTOR BASELINE` banner at the top of `PROJECT_AUDIT.md` and synchronized all active documentation to `.NET 10 LTS`, 7 database tables (+ `__SchemaVersions`), and `52/52` passing xUnit tests.

---

### Finding F-05: Physical Hardware vs. Simulator Verification Matrix
- **Severity:** **P2 (Medium)**
- **Affected Files:**
  - `docs/COM_BRIDGE_RELIABILITY.md`
- **Implemented Fix:**
  - Added an explicit verification matrix in `docs/COM_BRIDGE_RELIABILITY.md` distinguishing behaviors verified via automated simulator/unit tests from behaviors that require physical RS-232/USB DataLogger hardware validation in the field:

| Behavior | Verified via Simulator / Automated Tests | Requires Physical RS-232/USB Hardware |
| :--- | :--- | :--- |
| WMI display string parsing (`ExtractRawPortName`) | **Yes** (`SerialTransportTests`) | Verified on Windows WMI |
| Handshake validation (`CMD,OK,LCZ,` / `CMD,OK,LSS,`) & false-positive rejection | **Yes** (`WebLayerTests` + E2E `COM77` regression test) | Electrical baud mismatch on physical UART |
| Command lifecycle (`Pending -> Dispatched -> Completed/Failed/TimedOut`) | **Yes** (`UserControllerTests`, `TelemetryAndSecurityTests`) | — |
| Non-idempotent command duplicate prevention (`WLE1`, `WLH1`, `WLS0`, `WLM`) | **Yes** (`BridgeEngine_IsNonIdempotentPhysicalCommand_ClassifiesCommandsAccurately`) | Physical flash erase electrical timing |
| 25s CancellationToken timeout & `SerialPort.Close()` unblock | **Yes** (Code path & cancellation registration) | Physical USB cable unplug mid-stream (`WLB`) |
| Adaptive idle polling backoff & exponential network error backoff | **Yes** (`BridgeEngine`) | — |

---

## 3. Final Verification Commands & Actual Results

- `dotnet restore SerialLogHub.sln` $\rightarrow$ **Succeeded (0 Errors)**
- `dotnet build SerialLogHub.sln -c Release --no-restore` $\rightarrow$ **Succeeded (0 Errors)**
- `dotnet test SerialLogHub.sln -c Release --no-build` $\rightarrow$ **52 Passed, 0 Failed, 0 Skipped (`2s`)**
