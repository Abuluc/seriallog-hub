# Phase Four — Persistent Server-Side Telemetry & Device Storage

**Project:** `SerialLogHub.Api` & `SerialLogHub.Web` (.NET 10 LTS)  
**Document Date:** 2026-11-22

---

## 1. Storage Architecture Overview

SerialLog Hub uses a **dual-tier telemetry storage model**:

1. **Server-Side Persistent Relational Storage (SQLite + EF Core 10):**
   - Stores registered devices (`Devices`) and historical telemetry measurements (`TelemetryRecords`) permanently on the server.
   - Automatically ingests `WLB` serial log dumps whenever `POST /api/user/UpdateCommand` completes a `WLB` read, and also supports direct ingestion via `POST /api/telemetry/ingest`.
2. **Browser-Local Offline Cache (IndexedDB `serialLogHubDB`):**
   - Retains the last 20 local CSV uploads and serial dumps inside the operator's browser for instant zero-latency switching and offline inspection.

---

## 2. Relational Database Schema (`AppDbContext`)

### `Devices` (`DeviceEntity`)
| Column | Type | Description |
| :--- | :--- | :--- |
| `Id` | `INTEGER PK` | Auto-increment device primary key |
| `UserEmail` | `TEXT NOT NULL` | Owner operator email (enforced by `OperatorScopeGuard`) |
| `DeviceName` | `TEXT NOT NULL` | Friendly device label (e.g., `DEMO_COLD_STORAGE_A1`) |
| `PortName` | `TEXT NOT NULL` | COM port identifier (e.g., `COM99 (Virtual DataLogger - Demo)`) |
| `SerialNumber` | `TEXT NOT NULL` | Hardware/virtual serial identifier |
| `CreatedAt` | `TEXT (UTC)` | First registration timestamp |
| `LastSeenUtc` | `TEXT (UTC)` | Most recent telemetry ingestion timestamp |

- **Unique Index:** `IX_Devices_UserEmail_PortName` on `(UserEmail, PortName)`

### `TelemetryRecords` (`TelemetryRecordEntity`)
| Column | Type | Description |
| :--- | :--- | :--- |
| `Id` | `INTEGER PK` | Auto-increment telemetry record primary key |
| `DeviceId` | `INTEGER FK` | Foreign key to `Devices(Id)` (`ON DELETE CASCADE`) |
| `UserEmail` | `TEXT NOT NULL` | Owner operator email |
| `PortName` | `TEXT NOT NULL` | Source COM port |
| `Temperature` | `REAL NOT NULL` | Measured temperature in `°C` |
| `Humidity` | `REAL NOT NULL` | Measured relative humidity in `%` |
| `RecordedAt` | `TEXT (UTC)` | Sensor measurement UTC timestamp |
| `IngestedAt` | `TEXT (UTC)` | Server ingestion UTC timestamp |
| `IsAnomaly` | `INTEGER (BOOL)` | Whether any statistical or threshold rule flagged this sample |
| `AnomalySeverity` | `TEXT NOT NULL` | `Normal`, `Low`, `Medium`, `High`, or `Critical` |
| `AnomalyMethod` | `TEXT NOT NULL` | e.g., `StaticThreshold`, `SuddenSpike`, `RollingZScore`, `SensorDrift` |
| `AnomalyExplanation` | `TEXT NOT NULL` | Human-readable explanation of the anomaly |
| `RawPayload` | `TEXT NULL` | Original `CMD,OK,...` serial frame |

- **Composite Indexes:**
  - `IX_TelemetryRecords_UserEmail_RecordedAt` on `(UserEmail, RecordedAt)`
  - `IX_TelemetryRecords_DeviceId_RecordedAt` on `(DeviceId, RecordedAt)`

---

## 3. Why SQLite is Optimal for Current Deployment Scale

- Single-file zero-configuration deployment on Render Free Tier, Docker volumes (`/data/serialloghub.db`), and local workstations.
- With composite B-tree indexes on `(DeviceId, RecordedAt)` and `(UserEmail, RecordedAt)`, date-range and device-comparison queries over 100,000+ rows execute in `< 15 ms` without requiring external PostgreSQL or TimescaleDB infrastructure.
- Deduplication on `(DeviceId, RecordedAt)` during `POST /api/telemetry/ingest` ensures repeated uploads of the same device dump do not bloat storage.
