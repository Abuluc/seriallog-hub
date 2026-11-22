# API Documentation (`API_DOCUMENTATION.md`)

**Base URL (Default Local):** `http://localhost:8080/api/`  
**Interactive Docs (Development):** `http://localhost:8080/swagger`

## Authentication Header

All endpoints except `GET /api/health` and `/swagger/*` require:

```http
X-API-KEY: <your-configured-api-key>
```

Missing or invalid keys return `401 Unauthorized`.

---

## 1. Health Endpoint

### `GET /api/health`
Returns service health and whether Demo Mode is active.

**Response `200 OK`:**
```json
{
  "service": "SerialLogHub.Api",
  "status": "Healthy",
  "demoMode": true,
  "timestampUtc": "2026-11-22T10:00:00Z"
}
```

---

## 2. Authentication & Profile Endpoints

### `POST /api/Auth/login`
Authenticates an operator by email, username, and SHA-256 hashed password (verified against salted PBKDF2 storage).

**Request:**
```json
{
  "email": "demo@serialloghub.local",
  "username": "demo_operator",
  "hashedPassword": "<sha256-hex-of-password>"
}
```

**Responses:** `200 OK`, `400 Bad Request` (invalid username / unverified email), `401 Unauthorized` (incorrect password), `404 Not Found` (user not found).

### `POST /api/auth/{culture}/register`
Registers a new user account. In Demo Mode (`DemoMode:Enabled = true`), accounts are automatically verified for immediate testing.

### `POST /api/auth/updatename`
Updates a user's username after verifying `currentPassword`.

**Request:**
```json
{
  "email": "demo@serialloghub.local",
  "newValue": "senior_operator",
  "currentPassword": "<sha256-hex-of-current-password>"
}
```

### `GET /api/Auth/profile?email={email}`
Returns the operator's `email`, `username`, and `isEmailVerified` status.

### `POST /api/Auth/{culture}/SendResetMail`
Generates a password reset token. Accepts either a raw JSON string `"user@example.com"` (legacy compatibility) or `{ "email": "user@example.com" }`.

### `POST /api/Auth/{culture}/resendverificationemail`
Regenerates the verification token for unverified accounts.

---

## 3. COM Port & Command Relay Endpoints

### `POST /api/user/updateports`
Called by `SerialLogHub.Desktop` whenever local COM ports change.

**Request:**
```json
{
  "userEmail": "demo@serialloghub.local",
  "ports": [
    "COM3 (USB-SERIAL CH340)",
    "COM99 (Virtual DataLogger - Demo)"
  ]
}
```

### `GET /api/user/getports?email={email}`
Called by `SerialLogHub.Web` to populate the COM port selector.

**Response `200 OK`:**
```json
{
  "ports": [
    "COM3 (USB-SERIAL CH340)",
    "COM99 (Virtual DataLogger - Demo)"
  ]
}
```

### `POST /api/user/SendCommand`
Queues a serial command from `SerialLogHub.Web` for `SerialLogHub.Desktop` to execute.

**Request:**
```json
{
  "email": "demo@serialloghub.local",
  "SelecetdPort": "COM99 (Virtual DataLogger - Demo)",
  "baudRate": 9600,
  "message": "TEST",
  "commandID": "53afe150-fc9e-47c5-b2a6-2e3cb59633b7",
  "response": "Response yok"
}
```
*(Note: `Port` is also accepted as an alias for legacy `SelecetdPort`.)*

### `GET /api/user/getcommands?email={email}`
Polled by `SerialLogHub.Desktop`. Automatically expires stale unprocessed commands older than 60 seconds, then returns the oldest active unprocessed command (`200 OK`) or `404 Not Found` if the queue is empty.

### `POST /api/user/UpdateCommand`
Called by `SerialLogHub.Desktop` after receiving the hardware response. Marks `IsProcessed = true` (idempotent guard prevents duplicate history writes) and archives a copy to `CommandHistories`.

**Request:**
```json
{
  "email": "demo@serialloghub.local",
  "commandID": "53afe150-fc9e-47c5-b2a6-2e3cb59633b7",
  "response": "CMD,OK,LSS,VIRTUAL_LOGGER_V1"
}
```

### `GET /api/user/history?email={email}&take=25`
Returns the most recent completed commands from `CommandHistories` (clamped to `1..100`) for the operator's **Command Audit Log**, including `port`, `baudRate`, `message`, `timestamp`, `responsePreview`, and `isError`.

### `GET /api/user/getresponses?email={email}&CommandID={commandId}`
Polled by `SerialLogHub.Web` (with bounded timeout) until `isProcessed` is `true`.

---

## 4. Web Bridge Webhooks & SignalR Hub (`SerialLogHub.Web`, Port 3000)

- **`POST /Home/AppCheck`** *(Header: `X-BRIDGE-KEY`)* — Receives Desktop Bridge heartbeat and broadcasts `ReceiveMessage("ConsoleApp", "BridgeOnline")` over SignalR.
- **`POST /Home/waitresponse`** *(Header: `X-BRIDGE-KEY`)* — Receives streaming row-count progress and final `WLB` CSV payload, forwarding `datareadding` over SignalR.
- **`/notificationHub`** *(Authenticated SignalR Hub)* — Pushes real-time bridge status and telemetry dump updates to the logged-in operator's browser session.
