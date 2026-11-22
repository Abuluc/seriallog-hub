# System Architecture (`ARCHITECTURE.md`)

## 1. High-Level Three-Tier Architecture

**SerialLog Hub** separates browser-based visualization, backend command/state orchestration, and local Windows COM port hardware access into three decoupled .NET 8 applications:

```mermaid
flowchart LR
    Browser["Web Browser\n(uPlot, IndexedDB, ExcelJS, SignalR Client)"]
    Web["SerialLogHub.Web\n(ASP.NET Core 8 MVC + SignalR Hub)\nPort 3000"]
    API["SerialLogHub.Api\n(ASP.NET Core 8 Web API)\nPort 8080"]
    DB[("SQLite Database\nUsers, UsersPort, Commands,\nCommandHistories, ApiKeys")]
    Desktop["SerialLogHub.Desktop\n(.NET 8 Windows COM Port Bridge)\nRealSerialTransport / SimulatedSerialTransport"]
    HW["Physical DataLogger\n(USB-Serial COM Port)"]

    Browser <-->|"HTTPS/HTTP + Auth Cookie + SignalR"| Web
    Web <-->|"REST JSON + X-API-KEY"| API
    API <-->|"EF Core 8"| DB
    Desktop <-->|"REST JSON + X-API-KEY\n(Port Sync & Command Polling)"| API
    Desktop -->|"HTTP POST + X-BRIDGE-KEY\n(/Home/AppCheck, /Home/waitresponse)"| Web
    Desktop <-->|"System.IO.Ports\n(9600–115200 baud)"| HW
```

---

## 2. COM Port Discovery & Synchronization Flow

The Desktop Bridge continuously monitors local Windows COM ports (with WMI friendly device descriptions from `Win32_SerialPort`) and optionally exposes an isolated Virtual DataLogger for hardware-free demonstrations.

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
    Desk->>API: POST /api/user/updateports (X-API-KEY)
    API-->>Desk: 200 OK (Stored in UsersPort)
    Desk->>Web: POST /Home/AppCheck (X-BRIDGE-KEY)
    Web-->>UI: SignalR ReceiveMessage("ConsoleApp", "BridgeOnline")
    UI->>Web: GET /Home/Getavabileport
    Web->>API: GET /api/user/getports?email=...
    API-->>Web: { "ports": [...] }
    Web-->>UI: Populates COM Port dropdown
```

---

## 3. Device Configuration Command Flow (User -> DataLogger)

All device configuration screens (Description `WLG`, Delayed Start `WLS0`, Clock Sync `WLH1`, Flash Format `WLE1`, Alarm Thresholds `WLM`, Sampling Interval `WLA`, Auto-Start `WLV1`) follow the same reliable asynchronous command-relay pattern:

```mermaid
sequenceDiagram
    participant UI as Browser (Settings Page)
    participant Web as SerialLogHub.Web
    participant API as SerialLogHub.Api
    participant Desk as SerialLogHub.Desktop
    participant Dev as DataLogger (Real or Virtual)

    UI->>Web: POST /Settings/Settocomment (Cookie Auth)
    Web->>API: POST /api/user/SendCommand (CommandID, Port, "WLG, LAB_01")
    API-->>Web: 200 OK (Queued in Commands table)
    Desk->>API: GET /api/user/getcommands?email=...
    API-->>Desk: Pending CommandDto
    Desk->>Dev: WriteLine("WLG, LAB_01") -> ReadLine()
    Dev-->>Desk: "CMD,OK,COMM,LAB_01"
    Desk->>API: POST /api/user/UpdateCommand (Response, IsProcessed=true)
    Note over API: Archives row into CommandHistories
    Web->>API: Poll GET /api/user/getresponses?email=...&CommandID=... (bounded timeout)
    API-->>Web: { "isProcessed": true, "response": "CMD,OK,COMM,LAB_01" }
    Web-->>UI: JSON { status: "success", responseMessage: "CMD,OK,COMM,LAB_01" }
```

---

## 4. Telemetry Dump & Visualization Flow (DataLogger -> Web)

When the operator triggers a log dump (`WLB`), the Desktop Bridge streams serial chunks until the end-of-stream marker (`CMD,OK,LSD,`) or a 5-second inactivity timeout, pushing progress updates to the Web App's SignalR Hub and returning the complete CSV dataset.

```mermaid
sequenceDiagram
    participant UI as Browser Dashboard
    participant Web as SerialLogHub.Web
    participant API as SerialLogHub.Api
    participant Desk as SerialLogHub.Desktop
    participant Dev as DataLogger

    UI->>Web: POST /Home/Send { MessageToSend: "WLB" }
    Web->>API: POST /api/user/SendCommand
    Desk->>API: GET /api/user/getcommands
    Desk->>Dev: WriteLine("WLB")
    loop Until "CMD,OK,LSD," or 5s timeout
        Dev-->>Desk: CSV measurement chunks
        Desk->>Web: POST /Home/waitresponse (progress)
        Web-->>UI: SignalR datareadding("N rows read...")
    end
    Desk->>Web: POST /Home/waitresponse ("Finish", full CSV)
    Web-->>UI: SignalR datareadding("Finish", full CSV)
    Desk->>API: POST /api/user/UpdateCommand (full CSV)
    UI->>UI: Parse CSV -> Anomaly Detection -> uPlot Chart + IndexedDB + Table
```

---

## 5. Security Boundaries

- **No Direct Database Access from Clients:** Neither `SerialLogHub.Web` nor `SerialLogHub.Desktop` connects to SQLite/SQL Server directly. All persistence flows through `SerialLogHub.Api`.
- **Signed Cookie Identity:** `SerialLogHub.Web` resolves the active operator exclusively from the signed ASP.NET Core `ClaimTypes.Name` cookie claim (eliminating client-cookie spoofing).
- **Authenticated SignalR Hub:** `/notificationHub` requires `[Authorize]` and maps connections strictly by `Context.User.Identity.Name`.
- **Shared-Secret Protection:** API routes require `X-API-KEY`; Desktop-to-Web webhooks (`/Home/AppCheck`, `/Home/waitresponse`) require `X-BRIDGE-KEY`.
- **Salted PBKDF2 Password Storage:** Incoming SHA-256 client hashes are re-hashed with per-user random salt via ASP.NET Core `PasswordHasher<string>` before persistence.
