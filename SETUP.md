# Setup & Running Guide (`SETUP.md`)

## Prerequisites

- **.NET 8.0 SDK** (or newer, with Windows Desktop runtime for `SerialLogHub.Desktop`)
- **Windows 10 / 11** (required for `SerialLogHub.Desktop` WinForms + `System.IO.Ports` / WMI; `SerialLogHub.Api` and `SerialLogHub.Web` are cross-platform)
- No external SQL Server installation is required — `SerialLogHub.Api` uses **SQLite** automatically.

---

## 1. Build & Run Tests

From the repository root:

```powershell
dotnet build SerialLogHub.sln
dotnet test SerialLogHub.sln
```

---

## 2. Configure Secrets (Optional for Local Development)

Default development settings in `appsettings.Development.json` work out of the box with **Demo Mode enabled**.
For production deployments, copy `.env.example` values and replace `ApiKey`, `BridgeKey`, and `DemoMode:Enabled=false`.

---

## 3. Run All Three Applications Locally

Open three terminals at the repository root:

### Terminal 1 — Backend REST API (`http://localhost:8080`)
```powershell
dotnet run --project src/SerialLogHub.Api --urls http://localhost:8080
```
- Swagger UI is available at `http://localhost:8080/swagger`.
- On first startup in Development mode, SQLite (`serialloghub.db`) is created and seeded with the demo operator account:
  - **Email:** `demo@serialloghub.local`
  - **Username:** `demo_operator`
  - **Password:** `Demo1234!`

### Terminal 2 — Web Dashboard (`http://localhost:3000`)
```powershell
dotnet run --project src/SerialLogHub.Web --urls http://localhost:3000
```
- Open `http://localhost:3000` in your browser.
- Click **"Fill Demo Operator Credentials"** and sign in.

### Terminal 3 — Desktop COM Port Bridge (WinForms)
```powershell
dotnet run --project src/SerialLogHub.Desktop
```
- Click **"Fill Demo Operator"** and sign in.
- The Bridge automatically scans real Windows COM ports and, when **"Enable Virtual DataLogger (Demo)"** is checked, registers `COM99 (Virtual DataLogger - Demo)` so you can test the entire 3-tier hardware pipeline without a physical DataLogger!

*(Optional headless mode for automated testing: `dotnet run --project src/SerialLogHub.Desktop -- --headless --email demo@serialloghub.local`)*

---

## 4. Quick Demo Walkthrough (No Hardware Required)

1. Sign in to the Web Dashboard (`http://localhost:3000`).
2. **Instant Visualization:** Click **"Load Sample Demo Data"** on the dashboard to load 120 synthetic temperature/humidity readings with 2 injected anomalies, inspect the interactive **uPlot** chart, filter **Anomalies Only**, and click **Export .xlsx**.
3. **Full 3-Tier Virtual Device Test:** With `SerialLogHub.Desktop` running, select `COM99 (Virtual DataLogger - Demo)` in the Web Dashboard's **Serial Port Connection** panel, click **Connect**, verify the sidebar **Bridge Online** indicator and **Device Battery (87%)**, click **Read Logs (WLB)** to stream live data from the virtual device, and test any of the 6 **Device Configuration** screens!
