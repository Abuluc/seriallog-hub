# System Architecture (`ARCHITECTURE.md`)

## 1. High-Level Three-Tier Architecture (.NET 10 LTS)

**SerialLog Hub** separates browser-based visualization, backend command/telemetry orchestration, and local Windows COM port hardware access into three decoupled **.NET 10 LTS** applications:

```mermaid
flowchart LR
    Browser["Web Browser\n(uPlot, Chart.js, IndexedDB, ExcelJS, SignalR Client)"]
    Web["SerialLogHub.Web\n(ASP.NET Core 10 MVC + SignalR Hub)\nPort 3000"]
    API["SerialLogHub.Api\n(ASP.NET Core 10 Web API)\nPort 8080"]
    DB[("SQLite Database\nUsers, UsersPort, Commands,\nCommandHistories, ApiKeys,\nDevices, TelemetryRecords,\n__SchemaVersions")]
    Desktop["SerialLogHub.Desktop\n(.NET 10 Windows COM Port Bridge)\nRealSerialTransport / SimulatedSerialTransport"]
    HW["Physical DataLogger\n(USB-Serial COM Port)"]

    Browser <-->|"HTTPS/HTTP + Auth Cookie + SignalR"| Web
    Web <-->|"REST JSON + X-API-KEY + HMAC X-OPERATOR-PROOF"| API
    API <-->|"EF Core 10"| DB
    Desktop <-->|"REST JSON + X-API-KEY + X-OPERATOR-TOKEN"| API
    Desktop -->|"HTTP POST + X-BRIDGE-KEY\n(/Home/AppCheck, /Home/waitresponse)"| Web
    Desktop <-->|"System.IO.Ports\n(9600–115200 baud)"| HW
```

---

## 2. COM Port Discovery & Synchronization Flow

The Desktop Bridge continuously monitors local Windows COM ports (with WMI friendly device descriptions from `Win32_SerialPort`) and optionally exposes an isolated Virtual DataLogger (`COM99`) for hardware-free demonstrations.

```mermaid
sequenceDiagram
    participant OS as Windows Serial / WMI
    participant Desk as SerialLogHub.Desktop
    participant API as SerialLogHub.Api
    participant Web as SerialLogHub.Web
    participant UI as Browser Dashboard

    Desk->>OS: SerialPort.GetPortNames() + Win32_SerialPort
    OS-->>Desk: ["COM3 (USB-SERIAL CH340)", ...]
    Note over Desk: If Virtual Demo Mode enabled, appends<br/>"COM99 (Virtual DataLogger - Demo)"
    Desk->>API: POST /api/user/updateports (X-API-KEY + X-OPERATOR-TOKEN)
    API-->>Desk: 200 OK (Stored in UsersPort)
    Desk->>Web: POST /Home/AppCheck (X-BRIDGE-KEY)
    Web-->>UI: SignalR ReceiveMessage("ConsoleApp", "BridgeOnline")
    UI->>Web: GET /Home/GetAvailablePorts
    Web->>API: GET /api/user/getports?email=... (HMAC X-OPERATOR-PROOF)
    API-->>Web: { "ports": [...] }
    Web-->>UI: Populates COM Port dropdown
```

---

## 3. Device Configuration Command Flow (User -> DataLogger)

All 6 guided device configuration steps (Description `WLG`, Delayed Start `WLS0`, Clock Sync `WLH1`, Flash Format `WLE1`, Alarm Thresholds `WLM`, Sampling Interval `WLA`, Auto-Start `WLV1`) follow an explicit command lifecycle (`Pending -> Dispatched -> Completed / Failed / TimedOut`):

```mermaid
sequenceDiagram
    participant UI as Browser (Wizard /Settings)
    participant Web as SerialLogHub.Web
    participant API as SerialLogHub.Api
    participant Desk as SerialLogHub.Desktop
    participant Dev as DataLogger (Real or Virtual)

    UI->>Web: POST /Settings/SetComment (Cookie Auth)
    Web->>API: POST /api/user/SendCommand (CommandID, Port, "WLG, LAB_01")
    API-->>Web: 200 OK (Status = Pending)
    Desk->>API: GET /api/user/getcommands?email=... (X-OPERATOR-TOKEN)
    API-->>Desk: CommandDto (Status -> Dispatched, AttemptCount++)
    Desk->>Dev: WriteLine("WLG, LAB_01") -> ReadLine()
    Dev-->>Desk: "CMD,OK,COMM,LAB_01"
    Desk->>API: POST /api/user/UpdateCommand (Response, Status = Completed)
    Note over API: Archives row into CommandHistories
    Web->>API: Poll GET /api/user/getresponses?email=...&CommandID=...
    API-->>Web: { "isProcessed": true, "status": "Completed", "response": "CMD,OK,COMM,LAB_01" }
    Web-->>UI: JSON { status: "success", responseMessage: "CMD,OK,COMM,LAB_01" }
```

---

## 4. Telemetry Dump, Statistical Anomaly Analysis & Persistent SQLite Storage Flow

When the operator triggers a log dump (`WLB`), the Desktop Bridge streams serial chunks until the end-of-stream marker (`CMD,OK,LSD,`), pushing progress updates to the Web App's SignalR Hub (`DataReading`) and returning the complete CSV dataset to the API, which automatically runs the 6-method deterministic `AnomalyDetectionEngine` and persists records into `Devices` and `TelemetryRecords`.

---

## 5. Security Boundaries

- **Zero Direct Client Database Access:** Neither `SerialLogHub.Web` nor `SerialLogHub.Desktop` connects to SQLite directly. All persistence flows through `SerialLogHub.Api`.
- **Fail-Closed Operator Identity Binding (`OperatorScopeGuard`):**
  - Null authentication context, missing operator headers, or bare unverified `X-OPERATOR-EMAIL` headers always fail closed (`401 Unauthorized`).
  - `SerialLogHub.Desktop` receives a database-backed, SHA-256-hashed, operator-scoped session token (`X-OPERATOR-TOKEN`) upon login (`POST /api/auth/login`), bound to `ApiKeyEntity.OwnerEmail`.
  - `SerialLogHub.Web` signs proxied operator requests with a timestamped `HMAC-SHA256` signature (`X-OPERATOR-PROOF` + `X-OPERATOR-TIMESTAMP` within a $\pm 300\text{s}$ replay window).
  - Any mismatch between the authenticated credential owner and the requested `email` resource returns `403 Forbidden` (blocking BOLA/IDOR).
- **Production Startup Validation (`ProductionSecurityValidator`):** Rejects disabled API key checks, short keys (`< 16 chars`), and development fallback secrets (`dev-serialloghub-api-key`, `CHANGE_ME_IN_PRODUCTION`, `Demo1234!`) in Production.
- **Constant-Time Secret Comparison:** All API keys, Bridge keys, HMAC proofs, and password reset tokens are compared using `CryptographicOperations.FixedTimeEquals`.
