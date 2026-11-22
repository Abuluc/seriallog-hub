# Phase Six — SignalR & Real-Time Communication Architecture

**Hub:** `SerialLogHub.Web.Hubs.NotificationHub` (`/notificationHub`)  
**Document Date:** 2026-11-22

---

## 1. Connection Lifecycle & Authentication

1. **Strict Authorization (`[Authorize]`):**
   - `NotificationHub` requires a valid ASP.NET Core Cookie Authentication session (`CookieAuthenticationDefaults.AuthenticationScheme`). Unauthenticated WebSocket/SSE negotiation requests are rejected with `401 Unauthorized`.
2. **Identity Binding from Signed Claims Only:**
   - `OnConnectedAsync` extracts the operator's identity exclusively from `Context.User?.Identity?.Name` (never from client-supplied query strings or hub method parameters).
3. **Dual Registration (Multi-Tab + Native SignalR Groups):**
   - `UserConnectionManager` maintains a thread-safe `ConcurrentDictionary<string, HashSet<string>>` mapping each operator's normalized email to all active browser tab `ConnectionId`s.
   - Simultaneously, `OnConnectedAsync` enlists the connection into the SignalR user group `NotificationHub.GetUserGroupName(email)` (`user:{email}`).
   - Operators can also subscribe to specific device streams via `JoinDeviceGroup(portName)`, which scopes the group name to the authenticated operator (`device:{email}:{portName}`), preventing cross-user eavesdropping.

---

## 2. Event Contracts & Duplicate Message Protection

| Event Name | Direction | Payload | Purpose |
| :--- | :--- | :--- | :--- |
| `ReceiveMessage` | Server $\rightarrow$ Client | `(appName: "ConsoleApp", message: "BridgeOnline")` | Triggered by `POST /Home/AppCheck` (`X-BRIDGE-KEY` verified). Updates the sidebar & KPI bridge status to **Online** with a 6-second inactivity watchdog timer. |
| `DataReading` | Server $\rightarrow$ Client | `(appName: "ConsoleApp2", message: "<N> rows read..." \| "Finish", data: "<raw WLB CSV>")` | Triggered by `POST /Home/waitresponse` (`X-BRIDGE-KEY` verified). Streams live `WLB` row progress and delivers the final telemetry dump. |

### Duplicate Ingestion Prevention (`applySerialDumpOnce`)
When a `WLB` command completes, the browser can receive the telemetry payload via both SignalR (`DataReading` with `message == "Finish"`) and the HTTP response of `POST /Home/Send`. `wwwroot/js/dashboard/app.js` computes a fast content digest (`${rawCsv.length}:${rawCsv.slice(0, 64)}:${rawCsv.slice(-64)}`) and suppresses duplicate ingestion within a 4-second window (`lastWlbKey` / `lastWlbAppliedAt`).

---

## 3. Scalability & Redis Backplane Evaluation

- **Current Single-Instance Deployment (Render / Docker Compose):**
  - The built-in in-memory SignalR backplane + `UserConnectionManager` handles all concurrent connections with zero external infrastructure dependencies.
- **Future Multi-Instance Horizontal Scaling:**
  - Because `NotificationHub` now enlists connections into deterministic SignalR groups (`user:{email}` and `device:{email}:{portName}`), scaling `SerialLogHub.Web` to multiple replicas only requires adding `Microsoft.AspNetCore.SignalR.StackExchangeRedis` (`.AddStackExchangeRedis(redisConnectionString)`) without changing hub group naming or client JavaScript.
