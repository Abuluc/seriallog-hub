<div align="center">

# SerialLog Hub

**A three-tier DataLogger management platform for serial device configuration, COM port bridging, anomaly detection, and interactive telemetry visualization.**

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-MVC%20%2B%20Web%20API-0078D4)](https://learn.microsoft.com/aspnet/core/)
[![SignalR](https://img.shields.io/badge/SignalR-Real--Time-0ea5e9)](https://learn.microsoft.com/aspnet/core/signalr/)
[![SQLite](https://img.shields.io/badge/SQLite-EF%20Core%208-003B57?logo=sqlite&logoColor=white)](https://www.sqlite.org/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Live Demo](https://img.shields.io/badge/Live%20Demo-Render-46E3B7?logo=render&logoColor=black)](https://serialloghub-web.onrender.com)

---

### Live Demo / Canlı Demo

**[Launch Live Web Demo (`serialloghub-web.onrender.com`)](https://serialloghub-web.onrender.com)** &nbsp;|&nbsp; **[API Health Check (`serialloghub-api.onrender.com/api/health`)](https://serialloghub-api.onrender.com/api/health)**

*(Hosted on Render Free Tier — initial cold start may take 30–50 seconds. Use **"Fill Demo Operator Credentials"** on the login page.)*

### Choose Your Language / Dil Seçimi

**[Read Full Documentation in English (`README.en.md`)](README.en.md)** &nbsp;|&nbsp; **[Türkçe Detaylı Dokümantasyonu Oku (`README.tr.md`)](README.tr.md)**

</div>

---

## Quick Preview

![SerialLog Hub Telemetry Dashboard](docs/screenshots/02-dashboard-telemetry.png)

## System Overview

```mermaid
flowchart LR
    Browser["Web Browser\n(uPlot, IndexedDB, ExcelJS, SignalR)"]
    Web["SerialLogHub.Web\n(ASP.NET Core 8 MVC + SignalR)\n:3000"]
    API["SerialLogHub.Api\n(ASP.NET Core 8 REST API)\n:8080"]
    DB[("SQLite\nserialloghub.db")]
    Desktop["SerialLogHub.Desktop\n(.NET 8 Windows COM Bridge)"]
    Device["DataLogger Hardware\n(Real COM / Virtual Demo)"]

    Browser <-->|"HTTP + SignalR"| Web
    Web <-->|"REST + X-API-KEY"| API
    API <--> DB
    Desktop <-->|"REST + X-API-KEY"| API
    Desktop -->|"Webhooks + X-BRIDGE-KEY"| Web
    Desktop <-->|"SerialPort / Simulator"| Device
```

## Quick Start (Local Development)

```powershell
# Build and run automated tests
dotnet build SerialLogHub.sln
dotnet test SerialLogHub.sln

# Launch API (:8080), Web Dashboard (:3000), and Desktop COM Bridge
./scripts/run-local.ps1
```

## Documentation Index

- **[English Documentation (`README.en.md`)](README.en.md)** — Complete 17-section technical guide.
- **[Türkçe Dokümantasyon (`README.tr.md`)](README.tr.md)** — 17 bölümlük tam Türkçe rehber.
- **[Architecture (`docs/architecture/ARCHITECTURE.md`)](docs/architecture/ARCHITECTURE.md)** — Sequence diagrams and security boundaries.
- **[API Reference (`docs/api/API_DOCUMENTATION.md`)](docs/api/API_DOCUMENTATION.md)** — REST endpoints and SignalR Hub contract.
- **[Live Demo & Cloud Deployment (`docs/demo/DEMO_GUIDE.md`)](docs/demo/DEMO_GUIDE.md)** — Docker Compose, Render blueprint, and hosting guide.
- **[Security Policy (`SECURITY.md`)](SECURITY.md)** — Security controls and vulnerability reporting.
